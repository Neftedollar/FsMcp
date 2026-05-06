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

                    // Wait until subscribe message has been processed server-side.
                    let! subscribed =
                        waitUntilMs 5000 (fun () ->
                            registry.Subscribers.Count > 0 && registry.SessionServers.Count > 0)
                    Expect.isTrue subscribed "server-side subscribe is visible in registry within 5 s"

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

        testCase "H7: two clients subscribe to same URI populate distinct sessionIds (closes #8)" <| fun _ ->
            let run () =
                task {
                    // ── Arrange ──────────────────────────────────────────────

                    let uri =
                        ResourceUri.create "https://example.com/shared"
                        |> Result.defaultWith (fun e -> failtest $"URI error: %A{e}")
                    let mime =
                        MimeType.create "text/plain"
                        |> Result.defaultWith (fun e -> failtest $"MIME error: %A{e}")

                    let resourceDef : ResourceDefinition = {
                        Uri = uri
                        Name = "shared"
                        Description = None
                        MimeType = None
                        Handler = fun _ -> task { return Ok (TextResource (uri, mime, "shared data")) }
                    }

                    let config =
                        mcpServer {
                            name "HttpMultiClientSubscribeTest"
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

                    let testServer = app.GetTestServer()

                    // ── Build two independent HTTP/SSE MCP clients ────────────
                    let buildClient () =
                        task {
                            let httpClient = testServer.CreateClient()
                            httpClient.BaseAddress <- Uri("http://localhost")
                            let transportOptions =
                                HttpClientTransportOptions(Endpoint = Uri("http://localhost/mcp"))
                            let clientTransport =
                                new HttpClientTransport(transportOptions, httpClient, null, false)
                            let clientOptions = McpClientOptions()
                            clientOptions.ClientInfo <- Implementation(Name = "test-client", Version = "1.0.0")
                            let! sdkClient = McpClient.CreateAsync(clientTransport, clientOptions, null, cts.Token)
                            return (httpClient, clientTransport, sdkClient)
                        }

                    let! (httpA, transportA, clientA) = buildClient ()
                    let! (httpB, transportB, clientB) = buildClient ()

                    // ── Wire notification capture on each client ──────────────
                    let arrivalsA = System.Collections.Concurrent.ConcurrentBag<string>()
                    let arrivalsB = System.Collections.Concurrent.ConcurrentBag<string>()

                    let! subA =
                        clientA.SubscribeToResourceAsync(
                            "https://example.com/shared",
                            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>(
                                fun p _ct ->
                                    arrivalsA.Add(p.Uri)
                                    ValueTask.CompletedTask),
                            null,
                            cts.Token)

                    let! subB =
                        clientB.SubscribeToResourceAsync(
                            "https://example.com/shared",
                            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>(
                                fun p _ct ->
                                    arrivalsB.Add(p.Uri)
                                    ValueTask.CompletedTask),
                            null,
                            cts.Token)

                    // Wait for both subscribes to be observable on the server.
                    let! both =
                        waitUntilMs 5000 (fun () ->
                            registry.Subscribers.Count >= 2 && registry.SessionServers.Count >= 2)
                    Expect.isTrue both "both subscribes are observable in the registry within 5 s"

                    // ── Assert: distinct sessionIds populated ─────────────────
                    Expect.equal registry.Subscribers.Count 2 "two subscriber entries (one per client)"
                    Expect.equal registry.SessionServers.Count 2 "two distinct session servers tracked"

                    let sessionIds =
                        registry.SessionServers
                        |> Seq.map (fun kv -> kv.Key)
                        |> Seq.toList
                    match sessionIds with
                    | [ s1; s2 ] ->
                        Expect.notEqual s1 "" "first sessionId is non-empty"
                        Expect.notEqual s2 "" "second sessionId is non-empty"
                        Expect.notEqual s1 s2 "the two sessionIds are distinct"
                    | other -> failtest $"expected exactly 2 sessionIds, got %A{other}"

                    // Per-URI subscriber count: both entries reference `uri`.
                    let perUriBefore =
                        registry.Subscribers
                        |> Seq.filter (fun kv -> kv.Value.Uri = uri)
                        |> Seq.length
                    Expect.equal perUriBefore 2 "two subscribers for the shared URI before disconnect"

                    // ── Act: disconnect clientA only ──────────────────────────
                    do! subA.DisposeAsync()
                    do! clientA.DisposeAsync()
                    do! transportA.DisposeAsync()
                    httpA.Dispose()

                    // Wait for the server-side RunSessionHandler finally block to fire.
                    let! cleanedA =
                        waitUntilMs 5000 (fun () ->
                            registry.SessionServers.Count = 1 && registry.Subscribers.Count = 1)
                    Expect.isTrue cleanedA
                        "registry holds exactly client B's entries within 5 s after A disconnects"

                    Expect.equal registry.SessionServers.Count 1
                        "only B's session server remains after A disconnects"
                    let perUriAfter =
                        registry.Subscribers
                        |> Seq.filter (fun kv -> kv.Value.Uri = uri)
                        |> Seq.length
                    Expect.equal perUriAfter 1
                        "exactly one subscriber for the shared URI after A disconnects"

                    // ── Act: notifyChanged should reach B but not A ───────────
                    do! ResourceSubscriptions.notifyChanged uri registry

                    let! gotB =
                        waitUntilMs 5000 (fun () -> arrivalsB.Count >= 1)
                    Expect.isTrue gotB "client B receives the resource-updated notification"
                    Expect.equal arrivalsA.Count 0 "client A receives no notification after disconnect"

                    // ── Cleanup ───────────────────────────────────────────────
                    do! subB.DisposeAsync()
                    do! clientB.DisposeAsync()
                    do! transportB.DisposeAsync()
                    httpB.Dispose()

                    do! app.StopAsync(CancellationToken.None)
                }

            run () |> Async.AwaitTask |> Async.RunSynchronously

    ]
