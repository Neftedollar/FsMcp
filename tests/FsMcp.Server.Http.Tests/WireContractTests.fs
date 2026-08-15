#nowarn "57"

module FsMcp.Server.Http.Tests.WireContractTests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Sockets
open System.Security.Claims
open System.Text.Encodings.Web
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open ModelContextProtocol
open ModelContextProtocol.AspNetCore
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open FsMcp.Server.Http

type ProtocolArgs = { count: int; enabled: bool }

type WireAuthenticationHandler
    (options: IOptionsMonitor<AuthenticationSchemeOptions>,
     loggerFactory: ILoggerFactory,
     encoder: UrlEncoder) =
    inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)

    override this.HandleAuthenticateAsync() =
        let authorizationHeader = this.Request.Headers.Authorization
        let authorization = authorizationHeader.ToString()
        if authorization = "Bearer wire-test" then
            let identity =
                ClaimsIdentity(
                    [ Claim(ClaimTypes.NameIdentifier, "wire-user") ],
                    this.Scheme.Name)
            let ticket =
                AuthenticationTicket(ClaimsPrincipal(identity), this.Scheme.Name)
            Task.FromResult(AuthenticateResult.Success ticket)
        else
            Task.FromResult(AuthenticateResult.NoResult())

let private unwrap = function
    | Ok value -> value
    | Error error -> failtest $"%A{error}"

let private dictionary (values: (string * obj) list) =
    let result = Dictionary<string, obj>()
    values |> List.iter (fun (key, value) -> result[key] <- value)
    result :> IReadOnlyDictionary<string, obj>

let private startApp register cancellationToken = task {
    let builder = WebApplication.CreateBuilder()
    builder.Logging.SetMinimumLevel(LogLevel.Critical) |> ignore
    builder.WebHost.UseTestServer() |> ignore
    let registration = register builder.Services
    let app = builder.Build()
    app.MapMcp("/mcp") |> ignore
    do! app.StartAsync cancellationToken
    return app, registration
}

let private connect (app: WebApplication) cancellationToken = task {
    let httpClient = app.GetTestServer().CreateClient()
    httpClient.BaseAddress <- Uri("http://localhost")
    let transportOptions = HttpClientTransportOptions(Endpoint = Uri("http://localhost/mcp"))
    let transport = new HttpClientTransport(transportOptions, httpClient, null, false)
    let options = McpClientOptions()
    options.ClientInfo <- Implementation(Name = "wire-test", Version = "2.0")
    let! client = McpClient.CreateAsync(transport, options, null, cancellationToken)
    return httpClient, transport, client
}

let private connectWithHttpClient (httpClient: HttpClient) cancellationToken = task {
    let transportOptions = HttpClientTransportOptions(Endpoint = Uri(httpClient.BaseAddress, "/mcp"))
    let transport = new HttpClientTransport(transportOptions, httpClient, null, false)
    let options = McpClientOptions()
    options.ClientInfo <- Implementation(Name = "wire-test", Version = "2.0")
    let! client = McpClient.CreateAsync(transport, options, null, cancellationToken)
    return transport, client
}

let private waitUntil timeout predicate = task {
    let deadline = DateTime.UtcNow + timeout
    let mutable satisfied = predicate ()
    while not satisfied && DateTime.UtcNow < deadline do
        do! Task.Delay 25
        satisfied <- predicate ()
    return satisfied
}

let private subscriptionResource () =
    let uri = ResourceUri.create "https://example.com/live" |> unwrap
    let mime = MimeType.create "text/plain" |> unwrap
    let definition =
        Resource.define (ResourceUri.value uri) "live" (fun _ _ ->
            Task.FromResult(Ok (TextResource(uri, mime, "value"))))
        |> unwrap
    uri, definition

let private protocolFailure (operation: unit -> Task) = task {
    try
        do! operation ()
        return failtest "expected an MCP protocol error"
    with :? McpProtocolException as error ->
        return error
}

let private protocolConfig () =
    let mimeType = MimeType.create "text/plain" |> unwrap
    let typedTool =
        TypedTool.define<ProtocolArgs> "typed-tool" "typed tool" (fun args _ ->
            Task.FromResult(Ok [ Content.text $"{args.count}:{args.enabled}" ]))
        |> unwrap
    let typedResource =
        TypedResource.define<ProtocolArgs>
            "https://example.com/items/{count}/{enabled}"
            "typed resource"
            (fun args _ ->
                let uri = ResourceUri.create $"https://example.com/items/{args.count}/{args.enabled}" |> unwrap
                Task.FromResult(Ok (TextResource(uri, mimeType, $"{args.count}:{args.enabled}"))))
        |> unwrap
    let typedPrompt =
        TypedPrompt.define<ProtocolArgs> "typed-flags" "typed prompt" (fun args _ ->
            Task.FromResult(Ok [ { Role = User; Content = Content.text $"{args.count}:{args.enabled}" } ]))
        |> unwrap
    mcpServer {
        name "WireContracts"
        version "2.0"
        tool typedTool
        resource typedResource
        prompt typedPrompt
    }

let private primitiveServer () =
    let messages = Channel.CreateUnbounded<JsonRpcMessage>()
    let transport =
        { new ITransport with
            member _.SessionId = "primitive-test"
            member _.MessageReader = messages.Reader
            member _.SendMessageAsync(_, _) = Task.CompletedTask
            member _.DisposeAsync() = ValueTask.CompletedTask }
    let options = McpServerOptions()
    options.ServerInfo <- Implementation(Name = "primitive-test", Version = "2.0")
    let services = ServiceCollection().BuildServiceProvider()
    McpServer.Create(transport, options, NullLoggerFactory.Instance, services)

[<Tests>]
let wireContractTests =
    testList "HTTP wire contracts" [
        testCase "initialize returns the configured serverInfo exactly" <| fun _ ->
            let run () = task {
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
                let config = mcpServer {
                    name "ExactWireName"
                    version "2.7.3"
                }
                let! app, _ = startApp (HttpServer.addToServices config) timeout.Token
                use app = app
                let! httpClient, transport, client = connect app timeout.Token
                Expect.equal client.ServerInfo.Name "ExactWireName" "initialize name"
                Expect.equal client.ServerInfo.Version "2.7.3" "initialize version"
                do! client.DisposeAsync()
                do! transport.DisposeAsync()
                httpClient.Dispose()
                do! app.StopAsync CancellationToken.None
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "ASP.NET authorization rejects anonymous initialize and permits an authenticated client" <| fun _ ->
            let run () = task {
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
                let config = mcpServer {
                    name "AuthorizedWire"
                    version "2.0"
                }
                let builder = WebApplication.CreateBuilder()
                builder.Logging.SetMinimumLevel(LogLevel.Critical) |> ignore
                builder.WebHost.UseTestServer() |> ignore
                builder.Services
                    .AddAuthentication("wire")
                    .AddScheme<AuthenticationSchemeOptions, WireAuthenticationHandler>("wire", ignore)
                |> ignore
                builder.Services.AddAuthorization() |> ignore
                HttpServer.addToServices config builder.Services |> ignore
                let app = builder.Build()
                use app = app
                app.UseAuthentication() |> ignore
                app.UseAuthorization() |> ignore
                app.MapMcp("/mcp").RequireAuthorization() |> ignore
                do! app.StartAsync timeout.Token

                use anonymousClient = app.GetTestServer().CreateClient()
                anonymousClient.BaseAddress <- Uri("http://localhost")
                use initialize = new HttpRequestMessage(HttpMethod.Post, "/mcp")
                initialize.Headers.Accept.ParseAdd "application/json"
                initialize.Headers.Accept.ParseAdd "text/event-stream"
                initialize.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25") |> ignore
                initialize.Content <-
                    new StringContent(
                        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"anonymous","version":"1"}}}""",
                        Encoding.UTF8,
                        "application/json")
                use! unauthorized = anonymousClient.SendAsync(initialize, timeout.Token)
                Expect.equal unauthorized.StatusCode HttpStatusCode.Unauthorized "anonymous initialize"

                let authorizedClient = app.GetTestServer().CreateClient()
                authorizedClient.BaseAddress <- Uri("http://localhost")
                authorizedClient.DefaultRequestHeaders.Authorization <-
                    AuthenticationHeaderValue("Bearer", "wire-test")
                let! transport, client = connectWithHttpClient authorizedClient timeout.Token
                Expect.equal client.ServerInfo.Name "AuthorizedWire" "authorized initialize reaches MCP"
                do! client.DisposeAsync()
                do! transport.DisposeAsync()
                authorizedClient.Dispose()
                do! app.StopAsync CancellationToken.None
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "custom RunSessionHandler is invoked and still gets guaranteed cleanup" <| fun _ ->
            let run () = task {
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
                let uri, definition = subscriptionResource ()
                let config = mcpServer {
                    name "CustomSessionRunner"
                    version "2.0"
                    resource definition
                }
                let invoked = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                let completed = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                let configure (options: HttpServerTransportOptions) =
                    options.RunSessionHandler <-
                        Func<Microsoft.AspNetCore.Http.HttpContext, McpServer, CancellationToken, Task>(
                            fun _ server cancellationToken ->
                                task {
                                    invoked.TrySetResult() |> ignore
                                    try
                                        do! server.RunAsync cancellationToken
                                    finally
                                        completed.TrySetResult() |> ignore
                                }
                                :> Task)
                let! app, registration =
                    startApp (HttpServer.addToServicesWithOptions config configure) timeout.Token
                use app = app
                let registry = registration.Subscriptions |> Option.get
                let! httpClient, transport, client = connect app timeout.Token
                do! invoked.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                let! _subscription =
                    client.SubscribeToResourceAsync(
                        ResourceUri.value uri,
                        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>(fun _ _ ->
                            ValueTask.CompletedTask),
                        null,
                        timeout.Token)
                let! subscribed =
                    waitUntil (TimeSpan.FromSeconds 5.0) (fun () ->
                        ResourceSubscriptions.subscriptionCount registry = 1)
                Expect.isTrue subscribed "custom session registered the subscription"
                do! client.DisposeAsync()
                do! transport.DisposeAsync()
                httpClient.Dispose()
                do! completed.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                let! cleaned =
                    waitUntil (TimeSpan.FromSeconds 5.0) (fun () -> ResourceSubscriptions.isEmpty registry)
                Expect.isTrue cleaned "wrapper cleanup runs after the custom session handler"
                do! app.StopAsync CancellationToken.None
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "convenience HTTP runner cleans both subscription registries after abrupt disconnect" <| fun _ ->
            let run () = task {
                let probe = new TcpListener(IPAddress.Loopback, 0)
                probe.Start()
                let port = (probe.LocalEndpoint :?> IPEndPoint).Port
                probe.Stop()
                let url = $"http://127.0.0.1:{port}"
                let uri, definition = subscriptionResource ()
                let config = mcpServer {
                    name "ConvenienceCleanup"
                    version "2.0"
                    resource definition
                }
                use shutdown = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
                let registryReady =
                    TaskCompletionSource<ResourceSubscriptionRegistry>(TaskCreationOptions.RunContinuationsAsynchronously)
                let serverTask =
                    HttpServer.runWithSubscriptionsAndCancellation
                        config
                        (Some "/mcp")
                        url
                        (fun registry ->
                            match registry with
                            | Some value -> registryReady.TrySetResult value |> ignore
                            | None -> registryReady.TrySetException(InvalidOperationException("registry missing")) |> ignore
                            Task.FromResult())
                        shutdown.Token
                let! registry = registryReady.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                do! Task.Delay(500, shutdown.Token)
                let httpClient = new HttpClient(BaseAddress = Uri url)
                let! transport, client = connectWithHttpClient httpClient shutdown.Token
                let! _subscription =
                    client.SubscribeToResourceAsync(
                        ResourceUri.value uri,
                        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>(fun _ _ ->
                            ValueTask.CompletedTask),
                        null,
                        shutdown.Token)
                let! subscribed =
                    waitUntil (TimeSpan.FromSeconds 5.0) (fun () ->
                        ResourceSubscriptions.subscriptionCount registry = 1
                        && ResourceSubscriptions.trackedSessionCount registry = 1)
                Expect.isTrue subscribed "runner retained both subscription indexes"
                do! client.DisposeAsync()
                do! transport.DisposeAsync()
                httpClient.Dispose()
                let! cleaned =
                    waitUntil (TimeSpan.FromSeconds 5.0) (fun () ->
                        ResourceSubscriptions.subscriptionCount registry = 0
                        && ResourceSubscriptions.trackedSessionCount registry = 0)
                Expect.isTrue cleaned "abrupt disconnect clears subscriber and session-server indexes"
                shutdown.Cancel()
                try
                    do! serverTask
                with :? OperationCanceledException -> ()
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "SDK primitive bridges propagate cancellation without wrapping it" <| fun _ ->
            let run () = task {
                let toolToken = TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously)
                let resourceToken = TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously)
                let promptToken = TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously)
                let uri = ResourceUri.create "https://example.com/cancel" |> unwrap
                let mime = MimeType.create "text/plain" |> unwrap
                let toolDefinition =
                    Tool.define "cancel-tool" "cancel" (fun _ cancellationToken -> task {
                        toolToken.TrySetResult cancellationToken |> ignore
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return Ok []
                    }) |> unwrap
                let resourceDefinition =
                    Resource.define (ResourceUri.value uri) "cancel" (fun _ cancellationToken -> task {
                        resourceToken.TrySetResult cancellationToken |> ignore
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return Ok (TextResource(uri, mime, "unused"))
                    }) |> unwrap
                let promptDefinition =
                    Prompt.define "cancel-prompt" [] (fun _ cancellationToken -> task {
                        promptToken.TrySetResult cancellationToken |> ignore
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return Ok []
                    }) |> unwrap
                let server = primitiveServer ()
                let verify (observed: TaskCompletionSource<CancellationToken>) startRequest = task {
                    use requestCancellation = new CancellationTokenSource()
                    let pending: Task = startRequest requestCancellation.Token
                    let! handlerToken = observed.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                    Expect.equal handlerToken requestCancellation.Token "the supplied SDK token is passed exactly"
                    requestCancellation.Cancel()
                    let mutable cancelled = false
                    try
                        do! pending
                    with :? OperationCanceledException -> cancelled <- true
                    Expect.isTrue cancelled "cancellation is not converted to a handler error"
                }
                let toolContext =
                    RequestContext<CallToolRequestParams>(
                        server,
                        JsonRpcRequest(Method = "tools/call"),
                        CallToolRequestParams(Name = "cancel-tool"))
                let resourceContext =
                    RequestContext<ReadResourceRequestParams>(
                        server,
                        JsonRpcRequest(Method = "resources/read"),
                        ReadResourceRequestParams(Uri = ResourceUri.value uri))
                let promptContext =
                    RequestContext<GetPromptRequestParams>(
                        server,
                        JsonRpcRequest(Method = "prompts/get"),
                        GetPromptRequestParams(Name = "cancel-prompt"))
                do! verify toolToken (fun token -> Server.createSdkTool(toolDefinition).InvokeAsync(toolContext, token).AsTask() :> Task)
                do! verify resourceToken (fun token -> Server.createSdkResource(resourceDefinition).ReadAsync(resourceContext, token).AsTask() :> Task)
                do! verify promptToken (fun token -> Server.createSdkPrompt(promptDefinition).GetAsync(promptContext, token).AsTask() :> Task)
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "typed protocol values round-trip over Streamable HTTP" <| fun _ ->
            let run () = task {
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
                let! app, _ = startApp (HttpServer.addToServices (protocolConfig ())) timeout.Token
                use app = app
                let! httpClient, transport, client = connect app timeout.Token
                let! toolResult =
                    client.CallToolAsync(
                        "typed-tool",
                        dictionary [ "count", box 17; "enabled", box true ],
                        cancellationToken = timeout.Token)
                let toolText = toolResult.Content[0] :?> TextContentBlock
                Expect.equal toolText.Text "17:True" "typed tool"
                let! resourceResult =
                    client.ReadResourceAsync(
                        "https://example.com/items/17/true",
                        cancellationToken = timeout.Token)
                let resourceText = resourceResult.Contents[0] :?> TextResourceContents
                Expect.equal resourceText.Text "17:True" "typed resource string coercion"
                let! promptResult =
                    client.GetPromptAsync(
                        "typed-flags",
                        dictionary [ "count", box "17"; "enabled", box "true" ],
                        cancellationToken = timeout.Token)
                let promptText = promptResult.Messages[0].Content :?> TextContentBlock
                Expect.equal promptText.Text "17:True" "typed prompt string coercion"
                do! client.DisposeAsync()
                do! transport.DisposeAsync()
                httpClient.Dispose()
                do! app.StopAsync CancellationToken.None
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "invalid typed values are redacted InvalidParams errors" <| fun _ ->
            let run () = task {
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
                let! app, _ = startApp (HttpServer.addToServices (protocolConfig ())) timeout.Token
                use app = app
                let! httpClient, transport, client = connect app timeout.Token
                let invalidValue = "not-a-number-secret"
                let! toolError =
                    protocolFailure (fun () ->
                        client.CallToolAsync(
                            "typed-tool",
                            dictionary [ "count", box invalidValue; "enabled", box true ],
                            cancellationToken = timeout.Token).AsTask() :> Task)
                let! resourceError =
                    protocolFailure (fun () ->
                        client.ReadResourceAsync(
                            $"https://example.com/items/{invalidValue}/true",
                            cancellationToken = timeout.Token).AsTask() :> Task)
                let! promptError =
                    protocolFailure (fun () ->
                        client.GetPromptAsync(
                            "typed-flags",
                            dictionary [ "count", box invalidValue; "enabled", box "true" ],
                            cancellationToken = timeout.Token).AsTask() :> Task)
                [ toolError, "tool"; resourceError, "resource"; promptError, "prompt" ]
                |> List.iter (fun (error, primitive) ->
                    Expect.equal error.ErrorCode McpErrorCode.InvalidParams $"{primitive} code"
                    Expect.stringContains error.Message $"Invalid {primitive} arguments." $"{primitive} message"
                    Expect.isFalse (error.ToString().Contains invalidValue) $"{primitive} value is redacted")
                do! client.DisposeAsync()
                do! transport.DisposeAsync()
                httpClient.Dispose()
                do! app.StopAsync CancellationToken.None
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "stateless HTTP rejects subscriptions without retaining state" <| fun _ ->
            let run () = task {
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
                let config = protocolConfig ()
                let configure (options: HttpServerTransportOptions) = options.Stateless <- true
                let! app, registration =
                    startApp (HttpServer.addToServicesWithOptions config configure) timeout.Token
                use app = app
                let registry = registration.Subscriptions |> Option.get
                let! httpClient, transport, client = connect app timeout.Token
                Expect.isNotNull client.ServerCapabilities.Resources "resource capability"
                Expect.isFalse
                    (client.ServerCapabilities.Resources.Subscribe.GetValueOrDefault false)
                    "stateless subscribe is not advertised"
                use request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
                request.Headers.Accept.ParseAdd "application/json"
                request.Headers.Accept.ParseAdd "text/event-stream"
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25") |> ignore
                request.Content <-
                    new StringContent(
                        """{"jsonrpc":"2.0","id":99,"method":"resources/subscribe","params":{"uri":"https://example.com/items/17/true"}}""",
                        Encoding.UTF8,
                        "application/json")
                use! response = httpClient.SendAsync(request, timeout.Token)
                let! responseBody = response.Content.ReadAsStringAsync(timeout.Token)
                Expect.equal response.StatusCode HttpStatusCode.OK "JSON-RPC rejection uses HTTP 200"
                let responseJsonText =
                    responseBody.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryPick (fun line ->
                        if line.StartsWith("data:", StringComparison.Ordinal) then
                            Some(line.Substring("data:".Length).Trim())
                        else None)
                    |> Option.defaultValue responseBody
                use responseJson = JsonDocument.Parse responseJsonText
                let protocolError =
                    match responseJson.RootElement.TryGetProperty "error" with
                    | true, error -> error
                    | false, _ -> failtest $"expected a JSON-RPC error, got: {responseJsonText}"
                Expect.equal
                    (protocolError.GetProperty("code").GetInt32())
                    (int McpErrorCode.InvalidRequest)
                    "stateless subscribe fails closed without a session"
                Expect.isTrue (ResourceSubscriptions.isEmpty registry) "rejection is mutation-free"
                do! client.DisposeAsync()
                do! transport.DisposeAsync()
                httpClient.Dispose()
                do! app.StopAsync CancellationToken.None
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously
    ]
