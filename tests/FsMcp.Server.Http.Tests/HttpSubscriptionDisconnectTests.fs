// FS0057: HttpServerTransportOptions.RunSessionHandler is experimental (SDK MCPEXP002).
// Suppressed here because this test file exercises that code path directly.
#nowarn "57"

module FsMcp.Server.Http.Tests.HttpSubscriptionDisconnectTests

open Expecto
open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open ModelContextProtocol.AspNetCore
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

// ─────────────────────────────────────────────────────────────────────────────
//  Helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Wait until predicate is true, polling every 100 ms, for up to timeoutMs.
let private waitUntilMs (timeoutMs: int) (predicate: unit -> bool) : Task<bool> =
    task {
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        let mutable result = predicate ()
        while not result && DateTime.UtcNow < deadline do
            do! Task.Delay(100)
            result <- predicate ()
        return result
    }

/// Build an in-process WebApplication with TestHost, wiring RunSessionHandler for cleanup.
/// Returns (app, registry option).
let private buildTestApp (config: ServerConfig) =
    let builder = WebApplication.CreateBuilder()
    builder.Logging.SetMinimumLevel(LogLevel.Critical) |> ignore
    builder.WebHost.UseTestServer() |> ignore

    let mcpBuilder = builder.Services.AddMcpServer()

    // Register tools, resources, prompts and capture the subscription registry.
    // Uses internal registerAllInternal (FsMcp.Server is InternalsVisibleTo this test assembly).
    let registry = Server.registerAllInternal mcpBuilder config

    // Wire RunSessionHandler — mirrors exactly what HttpServer.runWithSubscriptions does.
    mcpBuilder.WithHttpTransport(fun opts ->
        match registry with
        | Some reg ->
            opts.RunSessionHandler <-
                Func<Microsoft.AspNetCore.Http.HttpContext, ModelContextProtocol.Server.McpServer, CancellationToken, Task>(
                    fun _ctx server ct ->
                        task {
                            try
                                do! server.RunAsync(ct)
                            finally
                                let sessionId =
                                    server.SessionId
                                    |> Option.ofObj
                                    |> Option.defaultValue ""
                                if sessionId <> "" then
                                    ResourceSubscriptions.unsubscribeAllForSession sessionId reg
                        } :> Task)
        | None -> ()) |> ignore

    let app = builder.Build()
    app.MapMcp("/mcp") |> ignore
    (app, registry)

// ─────────────────────────────────────────────────────────────────────────────
//  Tests
// ─────────────────────────────────────────────────────────────────────────────

[<Tests>]
let httpSubscriptionDisconnectTests =
    testList "HTTP RunSessionHandler subscription cleanup" [

        testCase "registry session count drops to 0 after client disconnects (closes #5, HTTP half)" <| fun _ ->
            let run () =
                task {
                    // ── Arrange ──────────────────────────────────────────────

                    let uri =
                        ResourceUri.create "https://example.com/live"
                        |> Result.defaultWith (fun e -> failtest $"URI error: %A{e}")
                    let mime =
                        MimeType.create "text/plain"
                        |> Result.defaultWith (fun e -> failtest $"MIME error: %A{e}")

                    let resourceDef : ResourceDefinition = {
                        Uri = uri
                        Name = "live"
                        Description = None
                        MimeType = None
                        Handler = fun _ -> task { return Ok (TextResource (uri, mime, "live data")) }
                    }

                    let config =
                        mcpServer {
                            name "HttpDisconnectTest"
                            version "1.0"
                            resource resourceDef
                            useStdio
                        }

                    let (app, registryOpt) = buildTestApp config
                    use app = app

                    match registryOpt with
                    | None -> failtest "registry must be Some when config has resources"
                    | Some registry ->

                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    do! app.StartAsync(cts.Token)

                    // Create an HttpClient backed by the TestServer (in-process, no real TCP)
                    let testServer = app.GetTestServer()
                    let httpClient = testServer.CreateClient()
                    httpClient.BaseAddress <- Uri("http://localhost")

                    // Connect an MCP client via HTTP through the TestServer
                    let transportOptions = HttpClientTransportOptions(Endpoint = Uri("http://localhost/mcp"))
                    use clientTransport = new HttpClientTransport(transportOptions, httpClient, null, false)

                    let clientOptions = McpClientOptions()
                    clientOptions.ClientInfo <- Implementation(Name = "test-client", Version = "1.0.0")

                    let! sdkClient = McpClient.CreateAsync(clientTransport, clientOptions, null, cts.Token)

                    // Subscribe to the resource
                    let! subscription =
                        sdkClient.SubscribeToResourceAsync(
                            "https://example.com/live",
                            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>(
                                fun _p _ct -> ValueTask.CompletedTask),
                            null,
                            cts.Token)

                    // Allow subscribe message to be processed server-side
                    do! Task.Delay(400)

                    // ── Assert pre-disconnect ─────────────────────────────────
                    Expect.isTrue (registry.Subscribers.Count > 0) "registry has subscriber after subscribe"
                    Expect.isTrue (registry.SessionServers.Count > 0) "session server is tracked"

                    // ── Act: disconnect the client ────────────────────────────
                    // Dispose the subscription and the client to close all HTTP connections.
                    // This signals the server-side session to terminate: RunAsync inside
                    // RunSessionHandler returns, the finally block fires, and
                    // unsubscribeAllForSession is called.
                    do! subscription.DisposeAsync()
                    do! sdkClient.DisposeAsync()
                    httpClient.Dispose()

                    // ── Assert: registry must clear within 5 s ────────────────
                    let! cleaned =
                        waitUntilMs 5000 (fun () ->
                            registry.Subscribers.IsEmpty && registry.SessionServers.IsEmpty)
                    Expect.isTrue cleaned "registry is empty within 5 s after client disconnect"

                    do! app.StopAsync(CancellationToken.None)
                }

            run () |> Async.AwaitTask |> Async.RunSynchronously

    ]
