module FsMcp.Client.Tests.EnterpriseManagedAuthorizationTests

#nowarn "44"
#nowarn "57"

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open FsMcp.Client

let private resource = Uri("https://mcp.example.test/mcp")
let private authorizationServer = Uri("https://authorization.example.test/")
let private identityEndpoint = Uri("https://identity.example.test/token")
let private authorizationTokenEndpoint = Uri("https://authorization.example.test/token")

let private resultValue = function
    | Ok value -> value
    | Error errors -> failtestf "Expected valid enterprise authorization configuration, got %A" errors

let private identityProvider () =
    EnterpriseIdentityProvider.fromTokenEndpoint identityEndpoint "identity-client"
    |> resultValue

let private options () =
    EnterpriseManagedAuthorizationOptions.create
        resource
        authorizationServer
        "mcp-client"
        (identityProvider ())
        (fun _ _ -> Task.FromResult "identity-token")
    |> resultValue

type private DelegateHandler(send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()

    let mutable calls = 0
    member _.Calls = Volatile.Read(&calls)

    override _.SendAsync(request, cancellationToken) =
        Interlocked.Increment(&calls) |> ignore
        send request cancellationToken

let private jsonResponse statusCode json =
    let response = new HttpResponseMessage(statusCode)
    response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
    response

type private TokenFlowHandler
    (
        accessToken: int -> string,
        expiresIn: int option,
        beforeRequest: HttpRequestMessage -> CancellationToken -> Task
    ) =
    inherit HttpMessageHandler()

    let mutable identityExchanges = 0
    let mutable authorizationExchanges = 0

    new(accessToken, expiresIn) =
        new TokenFlowHandler(accessToken, expiresIn, fun _ _ -> Task.CompletedTask)

    member _.IdentityExchanges = Volatile.Read(&identityExchanges)
    member _.AuthorizationExchanges = Volatile.Read(&authorizationExchanges)

    override _.SendAsync(request, cancellationToken) =
        task {
            do! beforeRequest request cancellationToken

            if request.Method = HttpMethod.Get then
                return
                    jsonResponse
                        HttpStatusCode.OK
                        "{\"issuer\":\"https://authorization.example.test/\",\"token_endpoint\":\"https://authorization.example.test/token\"}"
            elif request.RequestUri = identityEndpoint then
                Interlocked.Increment(&identityExchanges) |> ignore
                return
                    jsonResponse
                        HttpStatusCode.OK
                        "{\"access_token\":\"jag-assertion\",\"issued_token_type\":\"urn:ietf:params:oauth:token-type:id-jag\",\"token_type\":\"N_A\"}"
            elif request.RequestUri = authorizationTokenEndpoint then
                let generation = Interlocked.Increment(&authorizationExchanges)
                let expiry =
                    expiresIn
                    |> Option.map (fun seconds -> $",\"expires_in\":{seconds}")
                    |> Option.defaultValue ""
                let serializedToken =
                    System.Text.Json.JsonSerializer.Serialize(accessToken generation)
                return
                    jsonResponse
                        HttpStatusCode.OK
                        $"{{\"access_token\":{serializedToken},\"token_type\":\"Bearer\"{expiry}}}"
            else
                return new HttpResponseMessage(HttpStatusCode.NotFound)
        }

type private NonSeekableMemoryStream(bytes: byte array) =
    inherit MemoryStream(bytes)
    override _.CanSeek = false
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.Position
        with get () = base.Position
        and set _ = raise (NotSupportedException())

type private TrackingAsyncDisposable() =
    let mutable disposeCount = 0
    member _.DisposeCount = Volatile.Read(&disposeCount)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            Interlocked.Increment(&disposeCount) |> ignore
            ValueTask()

type private StubSdkClient
    (
        requestFailure: exn option,
        responseResult: System.Text.Json.Nodes.JsonNode option,
        disposeOperation: (unit -> ValueTask) option
    ) =
    inherit ModelContextProtocol.Client.McpClient()

    let mutable disposeCount = 0

    new(requestFailure, responseResult) =
        StubSdkClient(requestFailure, responseResult, None)

    new() = StubSdkClient(None, None, None)

    member _.DisposeCount = Volatile.Read(&disposeCount)
    override _.ServerCapabilities = Unchecked.defaultof<_>
    override _.ServerInfo = Unchecked.defaultof<_>
    override _.ServerInstructions = null
    override _.Completion = Task.FromResult(Unchecked.defaultof<_>)
    override _.SessionId = null
    override _.NegotiatedProtocolVersion = null

    override _.SendRequestAsync(_request, _cancellationToken) =
        match requestFailure with
        | Some failure ->
            Task.FromException<ModelContextProtocol.Protocol.JsonRpcResponse>(failure)
        | None ->
            Task.FromResult(
                ModelContextProtocol.Protocol.JsonRpcResponse(
                    Result = (responseResult |> Option.toObj)))

    override _.SendMessageAsync(_message, _cancellationToken) = Task.CompletedTask
    override _.RegisterNotificationHandler(_method, _handler) =
        Unchecked.defaultof<IAsyncDisposable>

    override _.DisposeAsync() =
        Interlocked.Increment(&disposeCount) |> ignore
        match disposeOperation with
        | Some operation -> operation ()
        | None -> ValueTask()

let private expectFailure expected (operation: unit -> Task) =
    task {
        try
            do! operation ()
            return failtestf "Expected enterprise failure %A" expected
        with :? EnterpriseManagedAuthorizationException as caught ->
            Expect.equal caught.Failure expected "typed enterprise failure"
    }

let private captureException (operation: unit -> Task) =
    task {
        try
            do! operation ()
            return failtest "Expected operation to fail"
        with caught ->
            return caught
    }

let private captureEnterpriseFailure (operation: unit -> Task) =
    task {
        try
            do! operation ()
            return failtest "Expected enterprise operation to fail"
        with :? EnterpriseManagedAuthorizationException as caught ->
            return caught
    }

let private stubClient requestFailure responseResult redact =
    let sdkClient = new StubSdkClient(requestFailure, responseResult)
    let client: FsMcp.Client.McpClient = {
        Client = sdkClient
        Lifetime = McpClientLifetime(sdkClient, None, None, redact)
        RedactTransportFailures = redact
    }
    sdkClient, client

let private configuredOptions callback =
    EnterpriseManagedAuthorizationOptions.create
        resource
        authorizationServer
        "mcp-client"
        (identityProvider ())
        callback
    |> resultValue

let private flowAuthorization (flow: HttpMessageHandler) configured =
    EnterpriseManagedAuthorization.createWithHandlerForTesting flow configured

let private resourceClient authorization send =
    new HttpClient(
        new EnterpriseManagedAuthorizationHandler(
            authorization,
            new DelegateHandler(send)),
        true)

[<Tests>]
let enterpriseManagedAuthorizationTests =
    testList "Enterprise-managed authorization" [
        testCase "production constructors require HTTPS" <| fun _ ->
            Expect.isError
                (EnterpriseIdentityProvider.fromTokenEndpoint
                    (Uri("http://localhost:5001/token"))
                    "client")
                "production IdP endpoint"

            Expect.isError
                (EnterpriseManagedAuthorizationOptions.create
                    (Uri("http://localhost:5000/mcp"))
                    authorizationServer
                    "client"
                    (identityProvider ())
                    (fun _ _ -> Task.FromResult "token"))
                "production resource"

        testCase "development constructors allow loopback HTTP only" <| fun _ ->
            let provider =
                EnterpriseIdentityProvider.fromTokenEndpointForDevelopment
                    (Uri("http://127.0.0.1:5001/token"))
                    "identity-client"
                |> resultValue

            Expect.isOk
                (EnterpriseManagedAuthorizationOptions.createForDevelopment
                    (Uri("http://localhost:5000/mcp"))
                    (Uri("http://127.0.0.1:5002/"))
                    "mcp-client"
                    provider
                    (fun _ _ -> Task.FromResult "token"))
                "loopback endpoints"

            Expect.isError
                (EnterpriseIdentityProvider.fromIssuerForDevelopment
                    (Uri("http://identity.example.test/"))
                    "identity-client")
                "non-loopback HTTP"

        testCase "authorization server must be an authority-root issuer" <| fun _ ->
            for endpoint in [
                Uri("https://authorization.example.test/oauth")
                Uri("https://authorization.example.test/?tenant=one")
            ] do
                let result =
                    EnterpriseManagedAuthorizationOptions.create
                        resource
                        endpoint
                        "mcp-client"
                        (identityProvider ())
                        (fun _ _ -> Task.FromResult "token")

                Expect.isError result $"invalid authorization-server issuer: {endpoint}"

        testCase "endpoint validation rejects user-info and fragments" <| fun _ ->
            for endpoint in [
                Uri("https://user@identity.example.test/token")
                Uri("https://identity.example.test/token#fragment")
            ] do
                Expect.isError
                    (EnterpriseIdentityProvider.fromTokenEndpoint endpoint "identity-client")
                    $"unsafe endpoint: {endpoint}"

        testCase "opaque values never render credentials" <| fun _ ->
            let provider =
                identityProvider ()
                |> EnterpriseIdentityProvider.withClientSecret "do-not-render"
                |> resultValue

            let configured =
                EnterpriseManagedAuthorizationOptions.create
                    resource
                    authorizationServer
                    "mcp-client"
                    provider
                    (fun _ _ -> Task.FromResult "identity-token")
                |> resultValue
                |> EnterpriseManagedAuthorizationOptions.withMcpClientSecret "also-secret"
                |> resultValue

            Expect.equal (provider.ToString()) "EnterpriseIdentityProvider" "opaque provider"
            Expect.equal
                (configured.ToString())
                "EnterpriseManagedAuthorizationOptions"
                "opaque options"
            Expect.isFalse (configured.ToString().Contains("secret")) "no secret text"

        testCase "scope validation enforces OAuth grammar and aggregate bound" <| fun _ ->
            let provider = identityProvider ()

            for invalid in [ []; [ "" ]; [ "two words" ]; [ "line\nfeed" ]; [ null ] ] do
                Expect.isError
                    (EnterpriseIdentityProvider.withScopes invalid provider)
                    $"invalid scopes: %A{invalid}"

            Expect.isOk
                (EnterpriseIdentityProvider.withScopes [ "openid"; "urn:example:scope" ] provider)
                "valid OAuth scopes"

            Expect.isError
                (EnterpriseIdentityProvider.withScopes
                    [ String('a', 40_000); String('b', 40_000) ]
                    provider)
                "combined bound"

        testCase "duration and replay bounds fail closed" <| fun _ ->
            let configured = options ()

            Expect.isError
                (EnterpriseManagedAuthorizationOptions.withRefreshSkew
                    (TimeSpan.FromTicks(-1L))
                    configured)
                "negative refresh skew"

            Expect.isError
                (EnterpriseManagedAuthorizationOptions.withUnspecifiedTokenLifetime
                    TimeSpan.Zero
                    configured)
                "zero fallback lifetime"

            Expect.isError
                (EnterpriseManagedAuthorizationOptions.withTokenAcquisitionTimeout
                    TimeSpan.MaxValue
                    configured)
                "unbounded acquisition timeout"

            for bytes in [ 0L; 4L * 1024L * 1024L + 1L ] do
                Expect.isError
                    (EnterpriseManagedAuthorizationOptions.withMaxRetryBodyBytes bytes configured)
                    $"invalid replay bound: {bytes}"

        testCase "origin comparison includes scheme, IDN host, and port" <| fun _ ->
            let baseline = Uri("https://example.test:8443/mcp")
            Expect.isTrue
                (EnterpriseAuthorizationEndpoint.sameOrigin baseline (Uri("https://EXAMPLE.test:8443/other")))
                "host comparison is case-insensitive"
            Expect.isFalse
                (EnterpriseAuthorizationEndpoint.sameOrigin baseline (Uri("http://example.test:8443/mcp")))
                "scheme differs"
            Expect.isFalse
                (EnterpriseAuthorizationEndpoint.sameOrigin baseline (Uri("https://example.test:9443/mcp")))
                "port differs"

        testCase "token policy permits only exact metadata paths and trusted POST targets" <| fun _ ->
            let policy = EnterpriseManagedAuthorizationEndpointPolicy.token (options ()).Settings
            let metadata = Uri("https://authorization.example.test/.well-known/oauth-authorization-server")

            Expect.isTrue
                (EnterpriseAuthorizationEndpoint.isSafeTokenRequest policy HttpMethod.Get metadata)
                "standard metadata target"
            Expect.isFalse
                (EnterpriseAuthorizationEndpoint.isSafeTokenRequest
                    policy
                    HttpMethod.Get
                    (Uri(metadata.AbsoluteUri + "?tenant=secret")))
                "metadata query refused"
            Expect.isFalse
                (EnterpriseAuthorizationEndpoint.isSafeTokenRequest
                    policy
                    HttpMethod.Get
                    (Uri("https://authorization.example.test/anything")))
                "arbitrary GET refused"
            Expect.isTrue
                (EnterpriseAuthorizationEndpoint.isSafeTokenRequest policy HttpMethod.Post identityEndpoint)
                "exact direct IdP endpoint"
            Expect.isFalse
                (EnterpriseAuthorizationEndpoint.isSafeTokenRequest
                    policy
                    HttpMethod.Post
                    (Uri("https://identity.example.test/other")))
                "other direct IdP path refused"

        testCaseAsync "token exchange handler rejects an unsafe request before I/O" <| async {
            let inner = new DelegateHandler(fun _ _ ->
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
            let policy = EnterpriseManagedAuthorizationEndpointPolicy.token (options ()).Settings
            use client =
                new HttpClient(
                    new EnterpriseTokenExchangeSecurityHandler(inner, 1024L, policy),
                    true)

            do!
                expectFailure
                    EnterpriseManagedAuthorizationFailure.TokenExchangeFailed
                    (fun () -> client.GetAsync(Uri("https://evil.example.test/token")))
                |> Async.AwaitTask

            Expect.equal inner.Calls 0 "unsafe target never reaches the network handler"
        }

        testCaseAsync "metadata issuer and token endpoint are validated" <| async {
            let policy = EnterpriseManagedAuthorizationEndpointPolicy.token (options ()).Settings
            let metadata = Uri("https://authorization.example.test/.well-known/oauth-authorization-server")

            let send json =
                let inner = new DelegateHandler(fun _ _ ->
                    let response = new HttpResponseMessage(HttpStatusCode.OK)
                    response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
                    Task.FromResult response)
                let client =
                    new HttpClient(
                        new EnterpriseTokenExchangeSecurityHandler(inner, 4096L, policy),
                        true)
                client.GetAsync(metadata), client

            let trusted, trustedClient =
                send "{\"issuer\":\"https://authorization.example.test/\",\"token_endpoint\":\"https://authorization.example.test/token\"}"
            use _trustedClient = trustedClient
            use! response = trusted |> Async.AwaitTask
            Expect.equal response.StatusCode HttpStatusCode.OK "trusted metadata"

            let untrusted, untrustedClient =
                send "{\"issuer\":\"https://evil.example.test/\",\"token_endpoint\":\"https://evil.example.test/token\"}"
            use _untrustedClient = untrustedClient
            do!
                expectFailure
                    EnterpriseManagedAuthorizationFailure.TokenExchangeFailed
                    (fun () -> untrusted :> Task)
                |> Async.AwaitTask
        }

        testCaseAsync "token responses are bounded before deserialization" <| async {
            let inner = new DelegateHandler(fun _ _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new ByteArrayContent(Array.zeroCreate 1025)
                Task.FromResult response)
            let policy = EnterpriseManagedAuthorizationEndpointPolicy.token (options ()).Settings
            use client =
                new HttpClient(
                    new EnterpriseTokenExchangeSecurityHandler(inner, 1024L, policy),
                    true)

            do!
                expectFailure
                    EnterpriseManagedAuthorizationFailure.TokenResponseTooLarge
                    (fun () -> client.PostAsync(identityEndpoint, new StringContent("grant=x")))
                |> Async.AwaitTask
        }

        testCaseAsync "known request body is replayable with headers preserved" <| async {
            use request = new HttpRequestMessage(HttpMethod.Post, resource)
            request.Content <- new ByteArrayContent([| 1uy; 2uy; 3uy |])
            request.Content.Headers.ContentType <- MediaTypeHeaderValue("application/octet-stream")

            let! replay =
                EnterpriseRequestReplay.prepare request 3L CancellationToken.None
                |> Async.AwaitTask

            match replay with
            | Replayable bytes -> Expect.sequenceEqual bytes [| 1uy; 2uy; 3uy |] "buffered body"
            | other -> failtestf "Expected replayable body, got %A" other

            use cloned = EnterpriseRequestReplay.clone request replay
            let! clonedBytes = cloned.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            Expect.sequenceEqual clonedBytes [| 1uy; 2uy; 3uy |] "cloned body"
            Expect.equal
                cloned.Content.Headers.ContentType.MediaType
                "application/octet-stream"
                "content type"
        }

        testCaseAsync "oversized known body is sent once" <| async {
            use request = new HttpRequestMessage(HttpMethod.Post, resource)
            request.Content <- new ByteArrayContent(Array.zeroCreate 5)
            let! replay =
                EnterpriseRequestReplay.prepare request 4L CancellationToken.None
                |> Async.AwaitTask
            Expect.equal replay SendOnce "known oversized body"
        }

        testCaseAsync "oversized non-seekable body retains the probed prefix" <| async {
            use request = new HttpRequestMessage(HttpMethod.Post, resource)
            request.Content <- new StreamContent(new NonSeekableMemoryStream([| 1uy; 2uy; 3uy; 4uy; 5uy |]))
            let! replay =
                EnterpriseRequestReplay.prepare request 4L CancellationToken.None
                |> Async.AwaitTask
            Expect.equal replay SendOnce "unknown oversized body"
            let! body = request.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            Expect.sequenceEqual body [| 1uy; 2uy; 3uy; 4uy; 5uy |] "entire original body"
        }

        testCaseAsync "unsafe resource requests are rejected before token acquisition" <| async {
            let configured = options ()
            use tokenClient = new HttpClient(new DelegateHandler(fun _ _ ->
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))
            use authorization =
                EnterpriseManagedAuthorization.createWithUnvalidatedHttpClientForTesting
                    tokenClient
                    configured
                :> IDisposable
            let session = authorization :?> EnterpriseManagedAuthorization
            let resourceInner = new DelegateHandler(fun _ _ ->
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
            use invoker =
                new HttpMessageInvoker(
                    new EnterpriseManagedAuthorizationHandler(session, resourceInner),
                    true)

            let checks = [
                Uri("https://evil.example.test/mcp"), EnterpriseManagedAuthorizationFailure.ResourceOriginMismatch
                Uri("https://user@mcp.example.test/mcp"), EnterpriseManagedAuthorizationFailure.UnsafeRequestTarget
                Uri("https://mcp.example.test/mcp#fragment"), EnterpriseManagedAuthorizationFailure.UnsafeRequestTarget
            ]

            for target, expected in checks do
                use request = new HttpRequestMessage(HttpMethod.Get, target)
                do!
                    expectFailure expected (fun () -> invoker.SendAsync(request, CancellationToken.None))
                    |> Async.AwaitTask

            use authorized = new HttpRequestMessage(HttpMethod.Get, resource)
            authorized.Headers.Authorization <- AuthenticationHeaderValue("Bearer", "static")
            do!
                expectFailure
                    EnterpriseManagedAuthorizationFailure.StaticAuthorizationHeader
                    (fun () -> invoker.SendAsync(authorized, CancellationToken.None))
                |> Async.AwaitTask

            use hostOverride = new HttpRequestMessage(HttpMethod.Get, resource)
            hostOverride.Headers.Host <- "other.example.test"
            do!
                expectFailure
                    EnterpriseManagedAuthorizationFailure.HostHeaderOverride
                    (fun () -> invoker.SendAsync(hostOverride, CancellationToken.None))
                |> Async.AwaitTask

            Expect.equal resourceInner.Calls 0 "refused requests never reach the resource"
        }

        testCaseAsync "disposed authorization fails without token exchange" <| async {
            let configured = options ()
            use tokenClient = new HttpClient(new DelegateHandler(fun _ _ ->
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))
            let authorization =
                EnterpriseManagedAuthorization.createWithUnvalidatedHttpClientForTesting
                    tokenClient
                    configured
            (authorization :> IDisposable).Dispose()
            (authorization :> IDisposable).Dispose()
            let! result = authorization.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            Expect.equal
                result
                (Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed)
                "disposed failure"
        }

        testCaseAsync "caller-owned token HttpClient survives authorization disposal" <| async {
            use handler = new DelegateHandler(fun _ _ ->
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
            use callerClient = new HttpClient(handler, false)
            let authorization =
                EnterpriseManagedAuthorization.createWithUnvalidatedHttpClientForTesting
                    callerClient
                    (options ())

            (authorization :> IDisposable).Dispose()
            (authorization :> IDisposable).Dispose()
            use! response = callerClient.GetAsync(identityEndpoint) |> Async.AwaitTask
            Expect.equal response.StatusCode HttpStatusCode.OK "caller client remains usable"
            Expect.equal handler.Calls 1 "caller handler remains owned by the caller"
        }

        testCase "enterprise connection failure projection preserves only safe categories" <| fun _ ->
            let capture operation =
                try
                    operation ()
                    None
                with caught -> Some caught

            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()
            let callerCancellation = OperationCanceledException("caller", cancellation.Token)
            let cleanupFailure = InvalidOperationException("cleanup?secret=do-not-expose")

            let projectedCancellation =
                capture (fun () ->
                    McpClient.projectConnectionFailure
                        true
                        cancellation.Token
                        callerCancellation
                        (Some cleanupFailure)
                    |> ignore)
                |> Option.defaultWith (fun () -> failtest "caller cancellation was not rethrown")

            Expect.isTrue
                (obj.ReferenceEquals(projectedCancellation, callerCancellation))
                "caller cancellation wins over cleanup failure"

            let typedFailure =
                EnterpriseManagedAuthorizationException
                    EnterpriseManagedAuthorizationFailure.TokenExchangeFailed

            let projectedTyped =
                capture (fun () ->
                    McpClient.projectConnectionFailure
                        true
                        CancellationToken.None
                        typedFailure
                        (Some cleanupFailure)
                    |> ignore)
                |> Option.defaultWith (fun () -> failtest "typed enterprise failure was not rethrown")

            Expect.isTrue
                (obj.ReferenceEquals(projectedTyped, typedFailure))
                "an existing redacted enterprise failure is preserved"

            let transportFailure = InvalidOperationException("https://mcp.example.test/?secret=do-not-expose")
            let projectedTransport =
                capture (fun () ->
                    McpClient.projectConnectionFailure
                        true
                        CancellationToken.None
                        transportFailure
                        (Some cleanupFailure)
                    |> ignore)
                |> Option.defaultWith (fun () -> failtest "transport failure was not projected")

            match projectedTransport with
            | :? EnterpriseManagedAuthorizationException as projected ->
                Expect.equal
                    projected.Failure
                    EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed
                    "connection failure is typed"
                Expect.isFalse
                    (projected.ToString().Contains("do-not-expose"))
                    "primary and cleanup details are redacted"
            | other -> failtestf "Expected a redacted enterprise failure, got %A" other

        testCaseAsync "token handler refuses cross-origin, downgrade, and non-token methods" <| async {
            let inner = new DelegateHandler(fun _ _ ->
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
            let policy = EnterpriseManagedAuthorizationEndpointPolicy.token (options ()).Settings
            use invoker =
                new HttpMessageInvoker(
                    new EnterpriseTokenExchangeSecurityHandler(inner, 1024L, policy),
                    true)

            let refused = [
                HttpMethod.Post, Uri("https://evil.example.test/token")
                HttpMethod.Post, Uri("http://authorization.example.test/token")
                HttpMethod.Put, authorizationTokenEndpoint
                HttpMethod.Get, Uri("https://identity.example.test/token")
            ]

            for method, target in refused do
                use request = new HttpRequestMessage(method, target)
                do!
                    expectFailure
                        EnterpriseManagedAuthorizationFailure.TokenExchangeFailed
                        (fun () -> invoker.SendAsync(request, CancellationToken.None))
                    |> Async.AwaitTask

            Expect.equal inner.Calls 0 "refused token targets never reach I/O"
        }

        testCaseAsync "streaming token response without Content-Length is bounded" <| async {
            let inner = new DelegateHandler(fun _ _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <-
                    new StreamContent(
                        new NonSeekableMemoryStream(Array.zeroCreate 1025))
                Task.FromResult response)
            let policy = EnterpriseManagedAuthorizationEndpointPolicy.token (options ()).Settings
            use client =
                new HttpClient(
                    new EnterpriseTokenExchangeSecurityHandler(inner, 1024L, policy),
                    true)

            do!
                expectFailure
                    EnterpriseManagedAuthorizationFailure.TokenResponseTooLarge
                    (fun () -> client.PostAsync(identityEndpoint, new StringContent("grant=x")))
                |> Async.AwaitTask
        }

        testCaseAsync "fifty token waiters share one acquisition" <| async {
            let flow = new TokenFlowHandler((fun generation -> $"access-{generation}"), Some 3600)
            let mutable callbacks = 0
            let configured =
                configuredOptions (fun _ cancellationToken ->
                    task {
                        Interlocked.Increment(&callbacks) |> ignore
                        do! Task.Delay(25, cancellationToken)
                        return "identity-token"
                    })
            use authorization = flowAuthorization flow configured

            let! results =
                [| for _ in 1..50 -> authorization.GetAccessTokenAsync CancellationToken.None |]
                |> Task.WhenAll
                |> Async.AwaitTask

            Expect.equal callbacks 1 "identity callback is single-flight"
            Expect.equal flow.AuthorizationExchanges 1 "authorization exchange is single-flight"
            Expect.isTrue
                (results
                 |> Array.forall (function Ok lease -> lease.Value = "access-1" | Error _ -> false))
                "all waiters receive the same access token"
        }

        testCaseAsync "cache lifetime and refresh skew control reuse" <| async {
            let mutable callbacks = 0
            let callback _ _ =
                Interlocked.Increment(&callbacks) |> ignore
                Task.FromResult "identity-token"

            let cachedFlow = new TokenFlowHandler((fun generation -> $"cached-{generation}"), Some 3600)
            use cached = flowAuthorization cachedFlow (configuredOptions callback)
            let! _ = cached.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            let! _ = cached.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            Expect.equal callbacks 1 "unexpired token is reused"

            let refreshFlow = new TokenFlowHandler((fun generation -> $"refresh-{generation}"), Some 1)
            let refreshOptions =
                configuredOptions callback
                |> EnterpriseManagedAuthorizationOptions.withRefreshSkew (TimeSpan.FromSeconds 2.0)
                |> resultValue
            use refreshing = flowAuthorization refreshFlow refreshOptions
            let! _ = refreshing.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            let! _ = refreshing.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            Expect.equal refreshFlow.AuthorizationExchanges 2 "token inside refresh skew is not cached"
        }

        testCaseAsync "401 evicts and retries once with a fresh token" <| async {
            let flow = new TokenFlowHandler((fun generation -> $"access-{generation}"), Some 3600)
            use authorization =
                flowAuthorization flow (configuredOptions (fun _ _ -> Task.FromResult "identity-token"))
            let seen = ResizeArray<string>()
            let mutable requests = 0
            use client =
                resourceClient authorization (fun request _ ->
                    seen.Add(request.Headers.Authorization.Parameter)
                    let count = Interlocked.Increment(&requests)
                    Task.FromResult(
                        new HttpResponseMessage(
                            if count = 1 then HttpStatusCode.Unauthorized else HttpStatusCode.OK)))

            use! response = client.GetAsync(resource) |> Async.AwaitTask
            Expect.equal response.StatusCode HttpStatusCode.OK "retry succeeds"
            Expect.sequenceEqual seen [ "access-1"; "access-2" ] "fresh token used once"
            Expect.equal requests 2 "exactly one retry"
        }

        testCaseAsync "403 does not refresh or retry" <| async {
            let flow = new TokenFlowHandler((fun generation -> $"access-{generation}"), Some 3600)
            use authorization =
                flowAuthorization flow (configuredOptions (fun _ _ -> Task.FromResult "identity-token"))
            let mutable requests = 0
            use client =
                resourceClient authorization (fun _ _ ->
                    Interlocked.Increment(&requests) |> ignore
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))

            use! response = client.GetAsync(resource) |> Async.AwaitTask
            Expect.equal response.StatusCode HttpStatusCode.Forbidden "403 preserved"
            Expect.equal requests 1 "403 is never retried"
            Expect.equal flow.AuthorizationExchanges 1 "token remains cached"
        }

        testCaseAsync "oversized body is sent once and rejected token is evicted" <| async {
            let flow = new TokenFlowHandler((fun generation -> $"access-{generation}"), Some 3600)
            let configured =
                configuredOptions (fun _ _ -> Task.FromResult "identity-token")
                |> EnterpriseManagedAuthorizationOptions.withMaxRetryBodyBytes 4L
                |> resultValue
            use authorization = flowAuthorization flow configured
            let mutable requests = 0
            use client =
                resourceClient authorization (fun request cancellationToken ->
                    task {
                        Interlocked.Increment(&requests) |> ignore
                        let! body = request.Content.ReadAsByteArrayAsync(cancellationToken)
                        Expect.equal body.Length 5 "whole send-once body"
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    })

            use request = new HttpRequestMessage(HttpMethod.Post, resource)
            request.Content <- new ByteArrayContent(Array.zeroCreate 5)
            use! response = client.SendAsync(request) |> Async.AwaitTask
            Expect.equal response.StatusCode HttpStatusCode.Unauthorized "401 preserved"
            Expect.equal requests 1 "oversized body is not replayed"
            let! _ = authorization.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            Expect.equal flow.AuthorizationExchanges 2 "rejected token was evicted"
        }

        testCaseAsync "hard timeout retains single-flight until ignored work settles" <| async {
            let flow = new TokenFlowHandler((fun generation -> $"access-{generation}"), Some 0)
            let blocked = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable callbacks = 0
            let configured =
                configuredOptions (fun _ _ ->
                    Interlocked.Increment(&callbacks) |> ignore
                    blocked.Task)
                |> EnterpriseManagedAuthorizationOptions.withTokenAcquisitionTimeout(
                    TimeSpan.FromMilliseconds 50.0)
                |> resultValue
            use authorization = flowAuthorization flow configured

            let! first = authorization.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            let! second = authorization.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
            Expect.equal first (Error EnterpriseManagedAuthorizationFailure.TokenAcquisitionTimedOut) "first timeout"
            Expect.equal second (Error EnterpriseManagedAuthorizationFailure.TokenAcquisitionTimedOut) "shared timeout"
            Expect.equal callbacks 1 "timed-out work retains the single-flight slot"

            blocked.TrySetResult "late-identity-token" |> ignore
            let mutable attempts = 0
            while callbacks < 2 && attempts < 20 do
                attempts <- attempts + 1
                let! _ = authorization.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
                if callbacks < 2 then
                    do! Task.Delay 10 |> Async.AwaitTask

            Expect.equal callbacks 2 "new acquisition starts only after ignored work settles"
        }

        testCaseAsync "dispose cancels cooperative acquisition and releases ten waiters" <| async {
            let flow = new TokenFlowHandler((fun generation -> $"access-{generation}"), Some 3600)
            let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable callbacks = 0
            let configured =
                configuredOptions (fun _ cancellationToken ->
                    task {
                        Interlocked.Increment(&callbacks) |> ignore
                        started.TrySetResult() |> ignore
                        do! Task.Delay(Timeout.Infinite, cancellationToken)
                        return "unreachable"
                    })
            let authorization = flowAuthorization flow configured
            let pending = [| for _ in 1..10 -> authorization.GetAccessTokenAsync CancellationToken.None |]
            do! started.Task |> Async.AwaitTask
            (authorization :> IDisposable).Dispose()

            let! results = Task.WhenAll pending |> Async.AwaitTask
            Expect.isTrue
                (results
                 |> Array.forall ((=) (Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed)))
                "every waiter is released with the disposed failure"
            Expect.equal callbacks 1 "one cooperative worker"
        }

        testCaseAsync "dispose releases a waiter when callback ignores cancellation" <| async {
            let flow = new TokenFlowHandler((fun generation -> $"access-{generation}"), Some 3600)
            let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let blocked = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            let configured =
                configuredOptions (fun _ _ ->
                    started.TrySetResult() |> ignore
                    blocked.Task)
            let authorization = flowAuthorization flow configured
            let pending = authorization.GetAccessTokenAsync CancellationToken.None

            try
                do! started.Task |> Async.AwaitTask
                (authorization :> IDisposable).Dispose()
                let! completed = Task.WhenAny(pending :> Task, Task.Delay 1000) |> Async.AwaitTask
                Expect.isTrue (obj.ReferenceEquals(completed, pending)) "public waiter released"
                let! result = pending |> Async.AwaitTask
                Expect.equal result (Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed) "disposed result"
            finally
                blocked.TrySetResult "late-identity-token" |> ignore
        }

        testCaseAsync "unsafe access token never reaches the resource" <| async {
            for unsafeToken in [ "unsafe token"; "unsafe,token"; "unsafe\r\ntoken" ] do
                let flow = new TokenFlowHandler((fun _ -> unsafeToken), Some 3600)
                use authorization =
                    flowAuthorization flow (configuredOptions (fun _ _ -> Task.FromResult "identity-token"))
                let! result = authorization.GetAccessTokenAsync CancellationToken.None |> Async.AwaitTask
                Expect.equal
                    result
                    (Error EnterpriseManagedAuthorizationFailure.UnsafeAccessToken)
                    $"unsafe Bearer token refused: {unsafeToken}"
        }

        testCaseAsync "concurrent disconnect callers share one terminal outcome" <| async {
            let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let expected = InvalidOperationException("controlled client-disposal failure")
            let disposeOperation () =
                ValueTask(
                    task {
                        started.TrySetResult() |> ignore
                        do! release.Task
                        return raise expected
                    } :> Task)
            let sdk = new StubSdkClient(None, None, Some disposeOperation)
            let transport = new TrackingAsyncDisposable()
            let lifetime =
                McpClientLifetime(
                    sdk,
                    Some(transport :> IAsyncDisposable),
                    Some(TimeSpan.FromSeconds 1.0),
                    false)
            let first = lifetime.DisconnectAsync()
            let second = lifetime.DisconnectAsync()
            Expect.isTrue (obj.ReferenceEquals(first, second)) "same cleanup task"
            do! started.Task |> Async.AwaitTask
            release.TrySetResult() |> ignore
            let! firstFailure = captureException (fun () -> first :> Task) |> Async.AwaitTask
            let! secondFailure = captureException (fun () -> second :> Task) |> Async.AwaitTask
            let third = lifetime.DisconnectAsync()
            let! thirdFailure = captureException (fun () -> third :> Task) |> Async.AwaitTask
            Expect.isTrue (obj.ReferenceEquals(first, third)) "repeated caller gets same task"
            Expect.isTrue (obj.ReferenceEquals(firstFailure, expected)) "first original failure"
            Expect.isTrue (obj.ReferenceEquals(secondFailure, expected)) "second original failure"
            Expect.isTrue (obj.ReferenceEquals(thirdFailure, expected)) "repeated original failure"
            Expect.equal sdk.DisposeCount 1 "SDK client disposed once"
            Expect.equal transport.DisposeCount 1 "transport disposed once"
        }

        testCaseAsync "enterprise disconnect shares one redacted terminal failure" <| async {
            let secretFailure = InvalidOperationException("cleanup?secret=do-not-expose")
            let sdk =
                new StubSdkClient(
                    None,
                    None,
                    Some(fun () -> ValueTask(Task.FromException(secretFailure))))
            let transport = new TrackingAsyncDisposable()
            let lifetime =
                McpClientLifetime(
                    sdk,
                    Some(transport :> IAsyncDisposable),
                    Some(TimeSpan.FromSeconds 1.0),
                    true)

            let first = lifetime.DisconnectAsync()
            let second = lifetime.DisconnectAsync()
            Expect.isTrue (obj.ReferenceEquals(first, second)) "same projected cleanup task"

            let! firstFailure = captureEnterpriseFailure (fun () -> first :> Task) |> Async.AwaitTask
            let! secondFailure = captureEnterpriseFailure (fun () -> second :> Task) |> Async.AwaitTask
            Expect.equal
                firstFailure.Failure
                EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed
                "disconnect failure is typed"
            Expect.isFalse (firstFailure.ToString().Contains "do-not-expose") "cleanup detail redacted"
            Expect.isTrue (obj.ReferenceEquals(firstFailure, secondFailure)) "shared terminal exception"
            Expect.equal sdk.DisposeCount 1 "SDK client disposed once"
            Expect.equal transport.DisposeCount 1 "transport disposed once"
        }

        testCaseAsync "independent cancellation is typed and enterprise-redacted" <| async {
            let secret = "internal-cancellation?credential=do-not-expose"
            let toolName =
                FsMcp.Core.Validation.ToolName.create "echo"
                |> function Ok value -> value | Error error -> failtestf "%A" error

            let _, ordinary = stubClient (Some(OperationCanceledException(secret))) None false
            let! ordinaryResult = McpClient.callTool ordinary toolName Map.empty |> Async.AwaitTask
            match ordinaryResult with
            | Error(FsMcp.Core.TransportError message) ->
                Expect.equal
                    message
                    "The MCP operation was canceled independently of the caller."
                    "typed independent cancellation"
            | other -> failtestf "Expected typed cancellation failure, got %A" other
            do! McpClient.disconnect ordinary |> Async.AwaitTask

            let _, enterprise = stubClient (Some(OperationCanceledException(secret))) None true
            let! failure = captureEnterpriseFailure (fun () -> McpClient.listTools enterprise) |> Async.AwaitTask
            Expect.equal
                failure.Failure
                EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed
                "enterprise redaction category"
            Expect.isFalse (failure.ToString().Contains secret) "secret omitted"
            do! McpClient.disconnect enterprise |> Async.AwaitTask
        }

        testCaseAsync "resource reads preserve every content item and empty lists" <| async {
            let requested =
                FsMcp.Core.Validation.ResourceUri.create "file:///requested"
                |> function Ok value -> value | Error error -> failtestf "%A" error
            let wrap (json: string) =
                stubClient
                    None
                    (Some(System.Text.Json.Nodes.JsonNode.Parse json))
                    false
                |> snd

            let multiple =
                wrap """{"contents":[{"uri":"file:///first","mimeType":"text/plain","text":"one"},{"uri":"file:///second","mimeType":"application/octet-stream","blob":"AQID"}]}"""
            let! result = McpClient.readResource multiple requested |> Async.AwaitTask
            match result with
            | Ok [ FsMcp.Core.TextResource(first, _, "one"); FsMcp.Core.BlobResource(second, _, data) ] ->
                Expect.equal (FsMcp.Core.Validation.ResourceUri.value first) "file:///first" "first URI"
                Expect.equal (FsMcp.Core.Validation.ResourceUri.value second) "file:///second" "second URI"
                Expect.sequenceEqual data [| 1uy; 2uy; 3uy |] "blob"
            | other -> failtestf "Expected two contents, got %A" other
            do! McpClient.disconnect multiple |> Async.AwaitTask

            let empty = wrap """{"contents":[]}"""
            let! emptyResult = McpClient.readResource empty requested |> Async.AwaitTask
            Expect.equal emptyResult (Ok []) "empty resource list succeeds"
            do! McpClient.disconnect empty |> Async.AwaitTask
        }

        testCaseAsync "prompt arguments are exposed and unknown roles fail closed" <| async {
            let wrap (json: string) =
                stubClient
                    None
                    (Some(System.Text.Json.Nodes.JsonNode.Parse json))
                    false
                |> snd
            let discovery =
                wrap """{"prompts":[{"name":"summarize","arguments":[{"name":"text","required":true}]}]}"""
            let! details = McpClient.listPromptDetails discovery |> Async.AwaitTask
            Expect.equal details.Head.Arguments.Head.Name "text" "argument name"
            Expect.isTrue details.Head.Arguments.Head.Required "required argument"
            do! McpClient.disconnect discovery |> Async.AwaitTask

            let unknown =
                wrap """{"messages":[{"role":42,"content":{"type":"text","text":"future"}}]}"""
            let promptName =
                FsMcp.Core.Validation.PromptName.create "summarize"
                |> function Ok value -> value | Error error -> failtestf "%A" error
            let! result = McpClient.getPrompt unknown promptName Map.empty |> Async.AwaitTask
            match result with
            | Error(FsMcp.Core.TransportError _) -> ()
            | other -> failtestf "Expected unknown role failure, got %A" other
            do! McpClient.disconnect unknown |> Async.AwaitTask
        }
    ]
