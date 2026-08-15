namespace FsMcp.Client

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks

type private PrefixAndTailContent(prefix: byte array, tail: Stream, owner: HttpContent) =
    inherit HttpContent()

    override _.SerializeToStreamAsync(target: Stream, _context: TransportContext) =
        task {
            do! target.WriteAsync(prefix.AsMemory()).AsTask()
            do! tail.CopyToAsync(target)
        }
        :> Task

    override _.SerializeToStreamAsync
        (target: Stream, _context: TransportContext, cancellationToken: CancellationToken)
        =
        task {
            do! target.WriteAsync(prefix.AsMemory(), cancellationToken).AsTask()
            do! tail.CopyToAsync(target, cancellationToken)
        }
        :> Task

    override _.TryComputeLength(length: byref<int64>) =
        length <- 0L
        false

    override this.Dispose(disposing) =
        if disposing then
            tail.Dispose()
            owner.Dispose()

        base.Dispose(disposing)

type internal ReplayableRequestBody =
    | NoBody
    | Replayable of body: byte array
    | SendOnce

module internal EnterpriseRequestReplay =

    let private copyContentHeaders (source: HttpContent) (target: HttpContent) =
        for header in source.Headers do
            if not (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) then
                target.Headers.TryAddWithoutValidation(header.Key, header.Value) |> ignore

    let private replaceWithBytes (request: HttpRequestMessage) (body: byte array) =
        let original = request.Content
        let replacement = new ByteArrayContent(body)
        copyContentHeaders original replacement
        request.Content <- replacement
        original.Dispose()

    let prepare
        (request: HttpRequestMessage)
        (maximumBytes: int64)
        (cancellationToken: CancellationToken)
        =
        task {
            match request.Content with
            | null -> return NoBody
            | content when content.Headers.ContentLength.HasValue
                           && content.Headers.ContentLength.Value > maximumBytes ->
                return SendOnce
            | content ->
                let! source = content.ReadAsStreamAsync(cancellationToken)
                use destination = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 81_920
                let probeLimit = maximumBytes + 1L
                let mutable complete = false

                while not complete && destination.Length < probeLimit do
                    let remaining = probeLimit - destination.Length
                    let count = min buffer.Length (int remaining)
                    let! read = source.ReadAsync(buffer.AsMemory(0, count), cancellationToken)
                    if read = 0 then complete <- true
                    else do! destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)

                let prefix = destination.ToArray()
                if complete && int64 prefix.Length <= maximumBytes then
                    replaceWithBytes request prefix
                    return Replayable prefix
                else
                    let replacement = new PrefixAndTailContent(prefix, source, content)
                    copyContentHeaders content replacement
                    request.Content <- replacement
                    return SendOnce
        }

    let clone (source: HttpRequestMessage) body =
        let target = new HttpRequestMessage(source.Method, source.RequestUri)
        target.Version <- source.Version
        target.VersionPolicy <- source.VersionPolicy

        for header in source.Headers do
            if not (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) then
                target.Headers.TryAddWithoutValidation(header.Key, header.Value) |> ignore

        for option in source.Options do
            target.Options.Set(HttpRequestOptionsKey<obj>(option.Key), option.Value)

        match body with
        | NoBody -> ()
        | Replayable bytes ->
            let content = new ByteArrayContent(bytes)
            if not (isNull source.Content) then copyContentHeaders source.Content content
            target.Content <- content
        | SendOnce -> invalidArg (nameof body) "A send-once body cannot be cloned."

        target

/// Bearer handler used by the MCP HTTP transport. Mutable authorization/cache
/// state stays behind the opaque authorization session; request preparation is
/// otherwise expressed as immutable replay decisions.
type internal EnterpriseManagedAuthorizationHandler
    (authorization: EnterpriseManagedAuthorization, innerHandler: HttpMessageHandler) =
    inherit DelegatingHandler(innerHandler)

    let failure value = raise (EnterpriseManagedAuthorizationException value)

    let getAccessToken cancellationToken =
        task {
            let! result = authorization.GetAccessTokenAsync cancellationToken
            match result with
            | Ok lease -> return lease
            | Error error -> return failure error
        }

    let validateRequest (request: HttpRequestMessage) =
        if isNull request.RequestUri
           || not (
               EnterpriseAuthorizationEndpoint.isSafeAtRuntime
                   authorization.ResourcePlaintextAllowance
                   request.RequestUri
           ) then
            failure EnterpriseManagedAuthorizationFailure.UnsafeRequestTarget

        if not (EnterpriseAuthorizationEndpoint.sameOrigin authorization.Settings.Resource request.RequestUri) then
            failure EnterpriseManagedAuthorizationFailure.ResourceOriginMismatch

        if not (isNull request.Headers.Authorization)
           || request.Headers
              |> Seq.exists (fun header ->
                  header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) then
            failure EnterpriseManagedAuthorizationFailure.StaticAuthorizationHeader

        if not (String.IsNullOrEmpty request.Headers.Host) then
            failure EnterpriseManagedAuthorizationFailure.HostHeaderOverride

    let authorize (request: HttpRequestMessage) lease =
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", lease.Value)

    member internal _.SendInnerAsync(request, cancellationToken) =
        base.SendAsync(request, cancellationToken)

    override this.SendAsync(request, cancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            validateRequest request

            let! replay =
                EnterpriseRequestReplay.prepare
                    request
                    authorization.Settings.MaxRetryBodyBytes
                    cancellationToken

            let! initialToken = getAccessToken cancellationToken
            authorize request initialToken
            let! initialResponse = this.SendInnerAsync(request, cancellationToken)

            if initialResponse.StatusCode <> HttpStatusCode.Unauthorized then
                return initialResponse
            else
                authorization.InvalidateIfCurrent initialToken.Generation

                match replay with
                | SendOnce -> return initialResponse
                | NoBody
                | Replayable _ ->
                    initialResponse.Dispose()
                    use retryRequest = EnterpriseRequestReplay.clone request replay
                    let! retryToken = getAccessToken cancellationToken
                    authorize retryRequest retryToken
                    let! retryResponse = this.SendInnerAsync(retryRequest, cancellationToken)

                    if retryResponse.StatusCode = HttpStatusCode.Unauthorized then
                        authorization.InvalidateIfCurrent retryToken.Generation

                    return retryResponse
        }
