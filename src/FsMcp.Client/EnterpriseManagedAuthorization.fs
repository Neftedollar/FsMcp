namespace FsMcp.Client

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open ModelContextProtocol.Authentication

[<assembly: InternalsVisibleTo("FsMcp.Client.Tests")>]
[<assembly: InternalsVisibleTo("FsMcp.TaskApi.Tests")>]
do ()

/// Resource and authorization-server identifiers supplied to the enterprise
/// identity-token callback.
[<NoComparison>]
type EnterpriseAuthorizationContext = {
    Resource: Uri
    AuthorizationServer: Uri
}

/// A validation failure produced while building enterprise authorization.
[<RequireQualifiedAccess>]
type EnterpriseManagedAuthorizationConfigurationError =
    | MissingRequiredValue of fieldName: string
    | InvalidEndpoint of fieldName: string
    | InsecureEndpoint of fieldName: string
    | InvalidScope of fieldName: string
    | InvalidDuration of fieldName: string
    | DurationTooLarge of fieldName: string
    | InvalidSize of fieldName: string

/// A redacted runtime failure. Cases intentionally contain no credentials,
/// tokens, OAuth response bodies, or endpoint query strings.
[<RequireQualifiedAccess>]
type EnterpriseManagedAuthorizationFailure =
    | AuthorizationDisposed
    | UnsupportedTransport
    | StaticAuthorizationHeader
    | HostHeaderOverride
    | ResourceOriginMismatch
    | UnsafeRequestTarget
    | IdentityTokenProviderFailed
    | EmptyIdentityToken
    | TokenAcquisitionTimedOut
    | TokenExchangeFailed
    | ResourceConnectionFailed
    | EmptyAccessToken
    | UnsafeAccessToken
    | TokenResponseTooLarge

module internal EnterpriseManagedAuthorizationFailure =

    let message = function
        | EnterpriseManagedAuthorizationFailure.AuthorizationDisposed ->
            "Enterprise-managed authorization has been disposed."
        | EnterpriseManagedAuthorizationFailure.UnsupportedTransport ->
            "Enterprise-managed authorization requires an HTTP MCP transport."
        | EnterpriseManagedAuthorizationFailure.StaticAuthorizationHeader ->
            "A static Authorization header cannot be combined with enterprise-managed authorization."
        | EnterpriseManagedAuthorizationFailure.HostHeaderOverride ->
            "An explicit Host header cannot be combined with enterprise-managed authorization."
        | EnterpriseManagedAuthorizationFailure.ResourceOriginMismatch ->
            "The MCP endpoint origin does not match the configured resource origin."
        | EnterpriseManagedAuthorizationFailure.UnsafeRequestTarget ->
            "Enterprise-managed authorization refused an unsafe request target."
        | EnterpriseManagedAuthorizationFailure.IdentityTokenProviderFailed ->
            "The enterprise identity-token provider failed."
        | EnterpriseManagedAuthorizationFailure.EmptyIdentityToken ->
            "The enterprise identity-token provider returned an empty token."
        | EnterpriseManagedAuthorizationFailure.TokenAcquisitionTimedOut ->
            "Enterprise access-token acquisition timed out."
        | EnterpriseManagedAuthorizationFailure.TokenExchangeFailed ->
            "Enterprise access-token exchange failed."
        | EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed ->
            "The enterprise-authorized MCP connection failed."
        | EnterpriseManagedAuthorizationFailure.EmptyAccessToken ->
            "The authorization server returned an empty access token."
        | EnterpriseManagedAuthorizationFailure.UnsafeAccessToken ->
            "The authorization server returned an access token that is unsafe for a Bearer header."
        | EnterpriseManagedAuthorizationFailure.TokenResponseTooLarge ->
            "An enterprise authorization response exceeded the configured safety bound."

/// A sanitized exception raised when enterprise authorization cannot safely
/// authorize an HTTP request.
[<Sealed>]
type EnterpriseManagedAuthorizationException internal (failure: EnterpriseManagedAuthorizationFailure) =
    inherit Exception(EnterpriseManagedAuthorizationFailure.message failure)

    /// The machine-readable, redacted failure category.
    member _.Failure = failure

[<NoEquality; NoComparison>]
type internal EnterprisePlaintextEndpointAllowance = {
    Origins: Uri list
    ExactTargets: Uri list
}

module internal EnterprisePlaintextEndpointAllowance =
    let none = { Origins = []; ExactTargets = [] }

[<NoEquality; NoComparison>]
type internal EnterpriseTokenRequestPolicy = {
    MetadataIssuers: Uri list
    TokenOrigins: Uri list
    ExactTokenTargets: Uri list
}

[<NoEquality; NoComparison>]
type internal EnterpriseIdentityProviderSettings = {
    Issuer: Uri option
    TokenEndpoint: Uri option
    ClientId: string
    ClientSecret: string option
    Scopes: string option
}

/// Validated enterprise identity-provider configuration.
[<Sealed>]
type EnterpriseIdentityProvider internal (settings: EnterpriseIdentityProviderSettings) =
    member internal _.Settings = settings
    override _.ToString() = "EnterpriseIdentityProvider"

[<NoEquality; NoComparison>]
type internal EnterpriseManagedAuthorizationSettings = {
    Resource: Uri
    AuthorizationServer: Uri
    McpClientId: string
    McpClientSecret: string option
    Scopes: string option
    IdentityProvider: EnterpriseIdentityProviderSettings
    IdentityTokenProvider: EnterpriseAuthorizationContext -> CancellationToken -> Task<string>
    RefreshSkew: TimeSpan
    UnspecifiedTokenLifetime: TimeSpan
    TokenAcquisitionTimeout: TimeSpan
    MaxRetryBodyBytes: int64
}

/// Validated immutable options for one signed-in user, resource, and
/// authorization-server combination.
[<Sealed>]
type EnterpriseManagedAuthorizationOptions internal (settings: EnterpriseManagedAuthorizationSettings) =
    member internal _.Settings = settings
    override _.ToString() = "EnterpriseManagedAuthorizationOptions"

type internal CachedEnterpriseAccessToken = {
    Value: string
    Generation: int64
    RefreshAt: DateTimeOffset
}

type internal EnterpriseAccessTokenLease = {
    Value: string
    Generation: int64
}

[<NoEquality; NoComparison>]
type private EnterpriseAcquisitionState =
    | Cached of Result<EnterpriseAccessTokenLease, EnterpriseManagedAuthorizationFailure>
    | Pending of Task<Result<EnterpriseAccessTokenLease, EnterpriseManagedAuthorizationFailure>>
    | Start of TaskCompletionSource<Result<EnterpriseAccessTokenLease, EnterpriseManagedAuthorizationFailure>>

type internal IdentityTokenCallbackException(failure: EnterpriseManagedAuthorizationFailure) =
    inherit Exception()
    member _.Failure = failure

type internal BoundedResponseException() =
    inherit Exception()

module internal EnterpriseAuthorizationValidation =

    [<Literal>]
    let MaxTextLength = 65_536

    let required fieldName (value: string) =
        if String.IsNullOrWhiteSpace value || value.Length > MaxTextLength then
            Error [ EnterpriseManagedAuthorizationConfigurationError.MissingRequiredValue fieldName ]
        else
            Ok value

    let optionalSecret fieldName value =
        required fieldName value |> Result.map Some

    let private validScopeCharacter (character: char) =
        let code = int character
        code = 0x21 || (code >= 0x23 && code <= 0x5B) || (code >= 0x5D && code <= 0x7E)

    let scopes fieldName (values: string list) =
        let materialized =
            if obj.ReferenceEquals(values, null) then [] else values

        let _, totalLength =
            ((true, 0L), materialized)
            ||> List.fold (fun (isFirst, total) value ->
                let valueLength =
                    if isNull value then int64 MaxTextLength + 1L else int64 value.Length

                let next = total + valueLength + if isFirst then 0L else 1L
                false, min next (int64 MaxTextLength + 1L))

        if List.isEmpty materialized
           || totalLength > int64 MaxTextLength
           || materialized
              |> List.exists (fun value ->
                  String.IsNullOrWhiteSpace value
                  || value.Length > MaxTextLength
                  || not (Seq.forall validScopeCharacter value)) then
            Error [ EnterpriseManagedAuthorizationConfigurationError.InvalidScope fieldName ]
        else
            Ok(Some(String.concat " " materialized))

    let duration fieldName maximum value =
        if value < TimeSpan.Zero then
            Error [ EnterpriseManagedAuthorizationConfigurationError.InvalidDuration fieldName ]
        elif value > maximum then
            Error [ EnterpriseManagedAuthorizationConfigurationError.DurationTooLarge fieldName ]
        else
            Ok value

    let positiveDuration fieldName maximum value =
        if value <= TimeSpan.Zero then
            Error [ EnterpriseManagedAuthorizationConfigurationError.InvalidDuration fieldName ]
        elif value > maximum then
            Error [ EnterpriseManagedAuthorizationConfigurationError.DurationTooLarge fieldName ]
        else
            Ok value

module internal EnterpriseAuthorizationEndpoint =

    let private validateResult error = Error [ error ]

    let validate allowLoopbackHttp fieldName (uri: Uri) =
        if isNull uri || not uri.IsAbsoluteUri || String.IsNullOrWhiteSpace uri.Host then
            validateResult (EnterpriseManagedAuthorizationConfigurationError.InvalidEndpoint fieldName)
        elif not (String.IsNullOrEmpty uri.UserInfo) || not (String.IsNullOrEmpty uri.Fragment) then
            validateResult (EnterpriseManagedAuthorizationConfigurationError.InvalidEndpoint fieldName)
        elif uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) then
            Ok uri
        elif allowLoopbackHttp
             && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
             && uri.IsLoopback then
            Ok uri
        elif uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) then
            validateResult (EnterpriseManagedAuthorizationConfigurationError.InsecureEndpoint fieldName)
        else
            validateResult (EnterpriseManagedAuthorizationConfigurationError.InvalidEndpoint fieldName)

    let sameOrigin (left: Uri) (right: Uri) =
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port = right.Port

    let sameTarget (left: Uri) (right: Uri) =
        String.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal)

    let isSafeAtRuntime allowance (uri: Uri) =
        match validate false "requestTarget" uri with
        | Ok _ -> true
        | Error _ ->
            not (isNull uri)
            && uri.IsAbsoluteUri
            && uri.IsLoopback
            && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (allowance.Origins |> List.exists (fun configured -> sameOrigin configured uri)
                || allowance.ExactTargets |> List.exists (fun configured -> sameTarget configured uri))

    let private metadataPaths (issuer: Uri) =
        let issuerPath = issuer.AbsolutePath.TrimEnd('/')
        issuerPath + "/.well-known/openid-configuration",
        "/.well-known/oauth-authorization-server" + issuerPath

    let metadataIssuerForRequest policy (uri: Uri) =
        policy.MetadataIssuers
        |> List.tryFind (fun issuer ->
            let oidcPath, oauthPath = metadataPaths issuer
            sameOrigin issuer uri
            && (String.Equals(uri.AbsolutePath, oidcPath, StringComparison.Ordinal)
                || String.Equals(uri.AbsolutePath, oauthPath, StringComparison.Ordinal))
            && String.IsNullOrEmpty uri.Query)

    let private hasAllowedScheme uri =
        validate true "requestTarget" uri |> Result.isOk

    let isSafeTokenRequest policy (method: HttpMethod) (uri: Uri) =
        hasAllowedScheme uri
        && if method = HttpMethod.Get then
               metadataIssuerForRequest policy uri |> Option.isSome
           elif method = HttpMethod.Post then
               policy.TokenOrigins |> List.exists (fun configured -> sameOrigin configured uri)
               || policy.ExactTokenTargets |> List.exists (fun configured -> sameTarget configured uri)
           else
               false

module EnterpriseIdentityProvider =

    let private createFromUri allowLoopbackHttp fieldName asIssuer uri clientId =
        let endpoint =
            EnterpriseAuthorizationEndpoint.validate allowLoopbackHttp fieldName uri
            |> Result.bind (fun validated ->
                if not asIssuer || String.IsNullOrEmpty validated.Query then Ok validated
                else Error [ EnterpriseManagedAuthorizationConfigurationError.InvalidEndpoint fieldName ])

        let validatedClientId =
            EnterpriseAuthorizationValidation.required "identityProviderClientId" clientId

        match endpoint, validatedClientId with
        | Ok validatedEndpoint, Ok validatedId ->
            Ok(
                EnterpriseIdentityProvider {
                    Issuer = if asIssuer then Some validatedEndpoint else None
                    TokenEndpoint = if asIssuer then None else Some validatedEndpoint
                    ClientId = validatedId
                    ClientSecret = None
                    Scopes = None
                }
            )
        | _ ->
            [ endpoint |> Result.map ignore; validatedClientId |> Result.map ignore ]
            |> List.collect (function Error errors -> errors | Ok _ -> [])
            |> Error

    /// Configure an HTTPS OIDC issuer whose metadata supplies the token endpoint.
    let fromIssuer issuer clientId =
        createFromUri false "identityProviderIssuer" true issuer clientId

    /// Configure an HTTPS token endpoint directly, without issuer discovery.
    let fromTokenEndpoint tokenEndpoint clientId =
        createFromUri false "identityProviderTokenEndpoint" false tokenEndpoint clientId

    /// Development-only issuer constructor. Plain HTTP is accepted only for a
    /// loopback URI.
    let fromIssuerForDevelopment issuer clientId =
        createFromUri true "identityProviderIssuer" true issuer clientId

    /// Development-only token-endpoint constructor. Plain HTTP is accepted only
    /// for a loopback URI.
    let fromTokenEndpointForDevelopment tokenEndpoint clientId =
        createFromUri true "identityProviderTokenEndpoint" false tokenEndpoint clientId

    /// Add a confidential-client secret. The returned value never renders it.
    let withClientSecret clientSecret (identityProvider: EnterpriseIdentityProvider) =
        EnterpriseAuthorizationValidation.optionalSecret
            "identityProviderClientSecret"
            clientSecret
        |> Result.map (fun secret ->
            let settings = identityProvider.Settings
            EnterpriseIdentityProvider { settings with ClientSecret = secret })

    /// Set the space-delimited IdP request scopes from validated OAuth scope tokens.
    let withScopes scopes (identityProvider: EnterpriseIdentityProvider) =
        EnterpriseAuthorizationValidation.scopes "identityProviderScopes" scopes
        |> Result.map (fun validatedScopes ->
            let settings = identityProvider.Settings
            EnterpriseIdentityProvider { settings with Scopes = validatedScopes })

module internal EnterpriseManagedAuthorizationEndpointPolicy =

    let resource settings =
        if settings.Resource.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) then
            { Origins = [ settings.Resource ]; ExactTargets = [] }
        else
            EnterprisePlaintextEndpointAllowance.none

    let token settings = {
        MetadataIssuers = [
            settings.AuthorizationServer
            match settings.IdentityProvider.Issuer with Some issuer -> issuer | None -> ()
        ]
        TokenOrigins = [
            settings.AuthorizationServer
            match settings.IdentityProvider.Issuer with Some issuer -> issuer | None -> ()
        ]
        ExactTokenTargets = [
            match settings.IdentityProvider.TokenEndpoint with Some endpoint -> endpoint | None -> ()
        ]
    }

module EnterpriseManagedAuthorizationOptions =

    let private maximumCacheDuration = TimeSpan.FromDays 365.0
    let private maximumAcquisitionTimeout = TimeSpan.FromHours 24.0
    let private maximumRetryBodyBytes = 4L * 1024L * 1024L

    let private collectErrors results =
        results |> List.collect (function Error errors -> errors | Ok _ -> [])

    let private createCore
        allowLoopbackHttp
        resource
        authorizationServer
        mcpClientId
        (identityProvider: EnterpriseIdentityProvider)
        identityTokenProvider
        =
        let resourceResult =
            EnterpriseAuthorizationEndpoint.validate allowLoopbackHttp "resource" resource

        let authorizationServerResult =
            EnterpriseAuthorizationEndpoint.validate
                allowLoopbackHttp
                "authorizationServer"
                authorizationServer
            |> Result.bind (fun validated ->
                if String.Equals(validated.AbsolutePath, "/", StringComparison.Ordinal)
                   && String.IsNullOrEmpty validated.Query then Ok validated
                else Error [ EnterpriseManagedAuthorizationConfigurationError.InvalidEndpoint "authorizationServer" ])

        let clientIdResult =
            EnterpriseAuthorizationValidation.required "mcpClientId" mcpClientId

        let identityProviderResult =
            if obj.ReferenceEquals(identityProvider, null) then
                Error [ EnterpriseManagedAuthorizationConfigurationError.MissingRequiredValue "identityProvider" ]
            else
                match identityProvider.Settings.Issuer, identityProvider.Settings.TokenEndpoint with
                | Some issuer, _ ->
                    EnterpriseAuthorizationEndpoint.validate
                        allowLoopbackHttp
                        "identityProviderIssuer"
                        issuer
                    |> Result.map (fun _ -> identityProvider)
                | _, Some tokenEndpoint ->
                    EnterpriseAuthorizationEndpoint.validate
                        allowLoopbackHttp
                        "identityProviderTokenEndpoint"
                        tokenEndpoint
                    |> Result.map (fun _ -> identityProvider)
                | None, None ->
                    Error [ EnterpriseManagedAuthorizationConfigurationError.MissingRequiredValue "identityProviderEndpoint" ]

        let identityTokenProviderResult =
            if obj.ReferenceEquals(identityTokenProvider, null) then
                Error [ EnterpriseManagedAuthorizationConfigurationError.MissingRequiredValue "identityTokenProvider" ]
            else
                Ok identityTokenProvider

        match
            resourceResult,
            authorizationServerResult,
            clientIdResult,
            identityProviderResult,
            identityTokenProviderResult
        with
        | Ok validatedResource, Ok validatedAuthorizationServer, Ok validatedClientId,
          Ok validatedIdentityProvider, Ok validatedTokenProvider ->
            Ok(
                EnterpriseManagedAuthorizationOptions {
                    Resource = validatedResource
                    AuthorizationServer = validatedAuthorizationServer
                    McpClientId = validatedClientId
                    McpClientSecret = None
                    Scopes = None
                    IdentityProvider = validatedIdentityProvider.Settings
                    IdentityTokenProvider = validatedTokenProvider
                    RefreshSkew = TimeSpan.FromSeconds 30.0
                    UnspecifiedTokenLifetime = TimeSpan.FromMinutes 5.0
                    TokenAcquisitionTimeout = TimeSpan.FromSeconds 30.0
                    MaxRetryBodyBytes = maximumRetryBodyBytes
                }
            )
        | _ ->
            [ resourceResult |> Result.map ignore
              authorizationServerResult |> Result.map ignore
              clientIdResult |> Result.map ignore
              identityProviderResult |> Result.map ignore
              identityTokenProviderResult |> Result.map ignore ]
            |> collectErrors
            |> Error

    /// Build production options. All configured endpoints must use HTTPS, and
    /// the MCP authorization-server issuer must be an authority-root URI.
    let create resource authorizationServer mcpClientId identityProvider identityTokenProvider =
        createCore false resource authorizationServer mcpClientId identityProvider identityTokenProvider

    /// Build development options. Plain HTTP is accepted only for loopback
    /// endpoints; the authorization-server issuer must remain authority-root.
    let createForDevelopment resource authorizationServer mcpClientId identityProvider identityTokenProvider =
        createCore true resource authorizationServer mcpClientId identityProvider identityTokenProvider

    /// Add the MCP authorization-server confidential-client secret.
    let withMcpClientSecret clientSecret (options: EnterpriseManagedAuthorizationOptions) =
        EnterpriseAuthorizationValidation.optionalSecret "mcpClientSecret" clientSecret
        |> Result.map (fun secret ->
            EnterpriseManagedAuthorizationOptions { options.Settings with McpClientSecret = secret })

    /// Set MCP authorization-server request scopes from OAuth scope tokens.
    let withScopes (scopes: string list) (options: EnterpriseManagedAuthorizationOptions) =
        EnterpriseAuthorizationValidation.scopes "mcpScopes" scopes
        |> Result.map (fun validatedScopes ->
            EnterpriseManagedAuthorizationOptions { options.Settings with Scopes = validatedScopes })

    /// Set the amount of lifetime reserved for proactive refresh.
    let withRefreshSkew value (options: EnterpriseManagedAuthorizationOptions) =
        EnterpriseAuthorizationValidation.duration "refreshSkew" maximumCacheDuration value
        |> Result.map (fun validated ->
            EnterpriseManagedAuthorizationOptions { options.Settings with RefreshSkew = validated })

    /// Set the bounded lifetime used when the authorization server omits expires_in.
    let withUnspecifiedTokenLifetime value (options: EnterpriseManagedAuthorizationOptions) =
        EnterpriseAuthorizationValidation.positiveDuration
            "unspecifiedTokenLifetime"
            maximumCacheDuration
            value
        |> Result.map (fun validated ->
            EnterpriseManagedAuthorizationOptions {
                options.Settings with UnspecifiedTokenLifetime = validated
            })

    /// Set the hard public wait bound for acquisition. Actual work retains the
    /// single-flight slot until it settles, even if a dependency ignores cancellation.
    let withTokenAcquisitionTimeout value (options: EnterpriseManagedAuthorizationOptions) =
        EnterpriseAuthorizationValidation.positiveDuration
            "tokenAcquisitionTimeout"
            maximumAcquisitionTimeout
            value
        |> Result.map (fun validated ->
            EnterpriseManagedAuthorizationOptions {
                options.Settings with TokenAcquisitionTimeout = validated
            })

    /// Set the replay buffer bound. The upper safety limit is 4 MiB.
    let withMaxRetryBodyBytes value (options: EnterpriseManagedAuthorizationOptions) =
        if value <= 0L || value > maximumRetryBodyBytes then
            Error [ EnterpriseManagedAuthorizationConfigurationError.InvalidSize "maxRetryBodyBytes" ]
        else
            Ok(
                EnterpriseManagedAuthorizationOptions {
                    options.Settings with MaxRetryBodyBytes = value
                }
            )

type internal EnterpriseTokenExchangeSecurityHandler
    (innerHandler: HttpMessageHandler, maximumResponseBytes: int64, requestPolicy: EnterpriseTokenRequestPolicy) =
    inherit DelegatingHandler(innerHandler)

    let bufferContent (content: HttpContent) cancellationToken =
        task {
            if content.Headers.ContentLength.HasValue
               && content.Headers.ContentLength.Value > maximumResponseBytes then
                raise (BoundedResponseException())

            use! source = content.ReadAsStreamAsync(cancellationToken)
            use destination = new IO.MemoryStream()
            let buffer = Array.zeroCreate<byte> 81_920
            let mutable complete = false

            while not complete && destination.Length <= maximumResponseBytes do
                let remaining = maximumResponseBytes + 1L - destination.Length
                let! read = source.ReadAsync(buffer.AsMemory(0, min buffer.Length (int remaining)), cancellationToken)
                if read = 0 then complete <- true
                else do! destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)

            if not complete || destination.Length > maximumResponseBytes then
                raise (BoundedResponseException())

            return destination.ToArray()
        }

    let tryUriProperty (name: string) (root: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        let mutable uri = Unchecked.defaultof<Uri>
        if root.TryGetProperty(name, &value)
           && value.ValueKind = JsonValueKind.String
           && Uri.TryCreate(value.GetString(), UriKind.Absolute, &uri) then Some uri
        else None

    let metadataIsTrusted requestUri body =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(body))
            let root = document.RootElement
            match
                EnterpriseAuthorizationEndpoint.metadataIssuerForRequest requestPolicy requestUri,
                tryUriProperty "issuer" root,
                tryUriProperty "token_endpoint" root
            with
            | Some configuredIssuer, Some responseIssuer, Some tokenEndpoint ->
                String.Equals(configuredIssuer.AbsoluteUri, responseIssuer.AbsoluteUri, StringComparison.Ordinal)
                && EnterpriseAuthorizationEndpoint.isSafeTokenRequest requestPolicy HttpMethod.Post tokenEndpoint
            | _ -> false
        with :? JsonException ->
            false

    member internal _.BufferContentAsync(content, cancellationToken) =
        bufferContent content cancellationToken

    member internal _.SendInnerAsync(request, cancellationToken) =
        base.SendAsync(request, cancellationToken)

    member internal _.MetadataIsTrusted(requestUri, body) =
        metadataIsTrusted requestUri body

    override this.SendAsync(request, cancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if isNull request.RequestUri
               || not (EnterpriseAuthorizationEndpoint.isSafeTokenRequest requestPolicy request.Method request.RequestUri) then
                raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.TokenExchangeFailed)

            let! response = this.SendInnerAsync(request, cancellationToken)
            try
                if response.Content |> isNull then
                    return response
                else
                    let! body = bufferContent response.Content cancellationToken

                    if request.Method = HttpMethod.Get
                       && not (metadataIsTrusted request.RequestUri body) then
                        response.Dispose()
                        return raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.TokenExchangeFailed)
                    else
                        let replacement = new ByteArrayContent(body)
                        for header in response.Content.Headers do
                            if not (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) then
                                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value) |> ignore
                        response.Content.Dispose()
                        response.Content <- replacement
                        return response
            with
            | :? BoundedResponseException ->
                response.Dispose()
                return raise (EnterpriseManagedAuthorizationException EnterpriseManagedAuthorizationFailure.TokenResponseTooLarge)
            | caught ->
                response.Dispose()
                return raise caught
        }

/// One enterprise-managed authorization session. Scope an instance to one
/// signed-in user and dispose it only after all clients sharing it disconnect.
[<Sealed>]
type EnterpriseManagedAuthorization internal
    (
        settings: EnterpriseManagedAuthorizationSettings,
        httpClient: HttpClient,
        ownsHttpClient: bool,
        loggerFactory: ILoggerFactory option
    ) =
    let synchronizationRoot = obj ()
    let lifetimeCts = new CancellationTokenSource()
    let lifetimeToken = lifetimeCts.Token
    let mutable disposed = false
    let mutable cachedToken: CachedEnterpriseAccessToken option = None
    let mutable inFlightAcquisition: Task<Result<EnterpriseAccessTokenLease, EnterpriseManagedAuthorizationFailure>> option = None
    let mutable nextGeneration = 0L

    let sdkOptions =
        IdentityAssertionGrantProviderOptions(
            ClientId = settings.McpClientId,
            IdpClientId = settings.IdentityProvider.ClientId,
            IdTokenCallback =
                IdentityAssertionGrantIdTokenCallback(fun context cancellationToken ->
                task {
                    try
                        let callbackContext = {
                            Resource = context.ResourceUrl
                            AuthorizationServer = context.AuthorizationServerUrl
                        }
                        let pending = settings.IdentityTokenProvider callbackContext cancellationToken
                        if isNull pending then
                            raise (IdentityTokenCallbackException EnterpriseManagedAuthorizationFailure.IdentityTokenProviderFailed)
                        let! identityToken = pending
                        if String.IsNullOrWhiteSpace identityToken then
                            raise (IdentityTokenCallbackException EnterpriseManagedAuthorizationFailure.EmptyIdentityToken)
                        if identityToken.Length > EnterpriseAuthorizationValidation.MaxTextLength
                           || identityToken <> identityToken.Trim()
                           || identityToken |> Seq.exists Char.IsControl then
                            raise (IdentityTokenCallbackException EnterpriseManagedAuthorizationFailure.IdentityTokenProviderFailed)
                        return identityToken
                    with
                    | :? IdentityTokenCallbackException as caught -> return raise caught
                    | :? OperationCanceledException as caught -> return raise caught
                    | _ ->
                        return raise (IdentityTokenCallbackException EnterpriseManagedAuthorizationFailure.IdentityTokenProviderFailed)
                }))

    do
        settings.McpClientSecret |> Option.iter (fun value -> sdkOptions.ClientSecret <- value)
        settings.Scopes |> Option.iter (fun value -> sdkOptions.Scope <- value)
        settings.IdentityProvider.Issuer
        |> Option.iter (fun value -> sdkOptions.IdpUrl <- value.AbsoluteUri)
        settings.IdentityProvider.TokenEndpoint
        |> Option.iter (fun value -> sdkOptions.IdpTokenEndpoint <- value.AbsoluteUri)
        settings.IdentityProvider.ClientSecret
        |> Option.iter (fun value -> sdkOptions.IdpClientSecret <- value)
        settings.IdentityProvider.Scopes
        |> Option.iter (fun value -> sdkOptions.IdpScope <- value)

    let provider = IdentityAssertionGrantProvider(sdkOptions, httpClient, null)
    let logger = loggerFactory |> Option.map (fun value -> value.CreateLogger<EnterpriseManagedAuthorization>())

    let safeAdd instant duration =
        if duration > DateTimeOffset.MaxValue - instant then DateTimeOffset.MaxValue
        else instant + duration

    let safeSubtract instant duration =
        if duration > instant - DateTimeOffset.MinValue then DateTimeOffset.MinValue
        else instant - duration

    let validateAccessToken (value: string) =
        let isTokenCharacter (character: char) =
            Char.IsAsciiLetterOrDigit character
            || character = '-'
            || character = '.'
            || character = '_'
            || character = '~'
            || character = '+'
            || character = '/'

        let withoutPadding = if isNull value then "" else value.TrimEnd('=')
        let containsOnlyTrailingPadding =
            not (isNull value)
            && (value |> Seq.skip withoutPadding.Length |> Seq.forall ((=) '='))
        let isBearerToken =
            withoutPadding.Length > 0
            && withoutPadding |> Seq.forall isTokenCharacter
            && containsOnlyTrailingPadding

        if String.IsNullOrWhiteSpace value then
            Error EnterpriseManagedAuthorizationFailure.EmptyAccessToken
        elif value.Length > EnterpriseAuthorizationValidation.MaxTextLength
             || value <> value.Trim()
             || value |> Seq.exists Char.IsControl
             || not isBearerToken then
            Error EnterpriseManagedAuthorizationFailure.UnsafeAccessToken
        else
            try
                AuthenticationHeaderValue("Bearer", value) |> ignore
                Ok value
            with _ ->
                Error EnterpriseManagedAuthorizationFailure.UnsafeAccessToken

    let acquireCore () =
        task {
            use timeoutCts = new CancellationTokenSource()
            timeoutCts.CancelAfter settings.TokenAcquisitionTimeout
            use acquisitionCts =
                CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifetimeToken)

            try
                if lifetimeToken.IsCancellationRequested then
                    return Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed
                else
                    provider.InvalidateCache()
                    let! token =
                        provider.GetAccessTokenAsync(
                            settings.Resource,
                            settings.AuthorizationServer,
                            acquisitionCts.Token
                        )

                    if lifetimeToken.IsCancellationRequested then
                        return Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed
                    elif timeoutCts.IsCancellationRequested then
                        return Error EnterpriseManagedAuthorizationFailure.TokenAcquisitionTimedOut
                    else
                        match validateAccessToken token.AccessToken with
                        | Error failure -> return Error failure
                        | Ok accessToken ->
                            let obtainedAt = token.ObtainedAt.ToUniversalTime()
                            let lifetime =
                                if token.ExpiresIn.HasValue then
                                    TimeSpan.FromSeconds(float token.ExpiresIn.Value)
                                else settings.UnspecifiedTokenLifetime
                            let expiresAt =
                                if lifetime <= TimeSpan.Zero then obtainedAt else safeAdd obtainedAt lifetime
                            let refreshAt = safeSubtract expiresAt settings.RefreshSkew

                            return
                                lock synchronizationRoot (fun () ->
                                    if disposed then
                                        Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed
                                    else
                                        nextGeneration <- nextGeneration + 1L
                                        let value = {
                                            Value = accessToken
                                            Generation = nextGeneration
                                            RefreshAt = refreshAt
                                        }
                                        if DateTimeOffset.UtcNow < refreshAt then
                                            cachedToken <- Some value
                                        Ok { Value = value.Value; Generation = value.Generation })
            with
            | :? IdentityTokenCallbackException as caught -> return Error caught.Failure
            | :? OperationCanceledException when lifetimeToken.IsCancellationRequested ->
                return Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed
            | :? OperationCanceledException when timeoutCts.IsCancellationRequested ->
                return Error EnterpriseManagedAuthorizationFailure.TokenAcquisitionTimedOut
            | :? OperationCanceledException as caught -> return raise caught
            | :? EnterpriseManagedAuthorizationException as caught -> return Error caught.Failure
            | _ -> return Error EnterpriseManagedAuthorizationFailure.TokenExchangeFailed
        }

    let getAcquisition () =
        lock synchronizationRoot (fun () ->
            if disposed then
                Cached(Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed)
            else
                match cachedToken with
                | Some token when DateTimeOffset.UtcNow < token.RefreshAt ->
                    Cached(Ok { Value = token.Value; Generation = token.Generation })
                | _ ->
                    cachedToken <- None
                    match inFlightAcquisition with
                    | Some pending -> Pending pending
                    | None ->
                        let completion =
                            TaskCompletionSource<Result<EnterpriseAccessTokenLease, EnterpriseManagedAuthorizationFailure>>(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            )
                        inFlightAcquisition <- Some completion.Task
                        Start completion)

    let startAcquisition
        (completion:
            TaskCompletionSource<
                Result<EnterpriseAccessTokenLease, EnterpriseManagedAuthorizationFailure>
             >)
        =
        let settle =
            task {
                let! result = acquireCore ()
                lock synchronizationRoot (fun () ->
                    match inFlightAcquisition with
                    | Some pending when obj.ReferenceEquals(pending, completion.Task) ->
                        inFlightAcquisition <- None
                    | _ -> ())
                completion.TrySetResult result |> ignore
            }
        ignore settle

    member internal _.Settings = settings
    member internal _.ResourcePlaintextAllowance =
        EnterpriseManagedAuthorizationEndpointPolicy.resource settings

    member private _.WaitForAcquisitionAsync
        (
            pending: Task<Result<EnterpriseAccessTokenLease, EnterpriseManagedAuthorizationFailure>>,
            cancellationToken: CancellationToken
        ) =
        task {
            use waitCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetimeToken)
            let timeout = Task.Delay(settings.TokenAcquisitionTimeout, waitCts.Token)
            let! _ = Task.WhenAny(pending, timeout)

            if cancellationToken.IsCancellationRequested then
                cancellationToken.ThrowIfCancellationRequested()
                return Unchecked.defaultof<_>
            elif lifetimeToken.IsCancellationRequested then
                return Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed
            elif pending.IsCompleted then
                waitCts.Cancel()
                return! pending
            else
                logger
                |> Option.iter (fun value ->
                    value.LogWarning("Enterprise access-token acquisition exceeded its configured timeout"))
                return Error EnterpriseManagedAuthorizationFailure.TokenAcquisitionTimedOut
        }

    member internal this.GetAccessTokenAsync(cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            match getAcquisition () with
            | Cached _ when lifetimeToken.IsCancellationRequested ->
                return Error EnterpriseManagedAuthorizationFailure.AuthorizationDisposed
            | Cached result -> return result
            | Pending pending -> return! this.WaitForAcquisitionAsync(pending, cancellationToken)
            | Start completion ->
                startAcquisition completion
                return! this.WaitForAcquisitionAsync(completion.Task, cancellationToken)
        }

    member internal _.InvalidateIfCurrent generation =
        lock synchronizationRoot (fun () ->
            match cachedToken with
            | Some current when current.Generation = generation -> cachedToken <- None
            | _ -> ())

    member private _.DisposeCore() =
        let shouldDispose =
            lock synchronizationRoot (fun () ->
                if disposed then false
                else
                    disposed <- true
                    cachedToken <- None
                    inFlightAcquisition <- None
                    true)

        if shouldDispose then
            try
                try lifetimeCts.Cancel() with _ -> ()
                if ownsHttpClient then httpClient.Dispose()
            finally
                lifetimeCts.Dispose()

    override _.ToString() = "EnterpriseManagedAuthorization"

    interface IDisposable with
        member this.Dispose() = this.DisposeCore()

/// Functions for creating an authorization session from validated options.
module EnterpriseManagedAuthorization =

    [<Literal>]
    let internal MaximumTokenResponseBytes = 1_048_576L

    let private createCore
        (options: EnterpriseManagedAuthorizationOptions)
        (httpClient: HttpClient)
        ownsHttpClient
        loggerFactory
        =
        if obj.ReferenceEquals(options, null) then nullArg (nameof options)
        if isNull httpClient then nullArg (nameof httpClient)
        new EnterpriseManagedAuthorization(options.Settings, httpClient, ownsHttpClient, loggerFactory)

    let private createOwnedFromHandler
        (options: EnterpriseManagedAuthorizationOptions)
        (handler: HttpMessageHandler)
        loggerFactory
        =
        if obj.ReferenceEquals(options, null) then nullArg (nameof options)
        let policy = EnterpriseManagedAuthorizationEndpointPolicy.token options.Settings
        let security = new EnterpriseTokenExchangeSecurityHandler(handler, MaximumTokenResponseBytes, policy)
        let httpClient = new HttpClient(security, true)
        httpClient.Timeout <- Timeout.InfiniteTimeSpan
        try createCore options httpClient true loggerFactory
        with _ ->
            httpClient.Dispose()
            reraise ()

    let private createOwned
        (options: EnterpriseManagedAuthorizationOptions)
        (loggerFactory: ILoggerFactory option)
        =
        if obj.ReferenceEquals(options, null) then nullArg (nameof options)
        let sockets = new SocketsHttpHandler()
        sockets.AllowAutoRedirect <- false
        sockets.UseCookies <- false
        sockets.AutomaticDecompression <- DecompressionMethods.None
        try createOwnedFromHandler options sockets loggerFactory
        with _ ->
            sockets.Dispose()
            reraise ()

    /// Create the safe default authorization session. Its token-exchange client
    /// disables redirects and bounds every response to 1 MiB.
    let create options = createOwned options None

    /// Create the safe default authorization session with redacted diagnostics.
    /// Options are final for F# pipeline composition.
    let createWithLogger (loggerFactory: ILoggerFactory) options =
        if isNull loggerFactory then nullArg (nameof loggerFactory)
        createOwned options (Some loggerFactory)

    let internal createWithUnvalidatedHttpClientForTesting
        (httpClient: HttpClient)
        options =
        createCore options httpClient false None

    let internal createWithHandlerForTesting
        (handler: HttpMessageHandler)
        options =
        createOwnedFromHandler options handler None
