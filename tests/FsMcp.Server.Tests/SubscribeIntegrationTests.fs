module FsMcp.Server.Tests.SubscribeIntegrationTests

open Expecto
open System
open System.IO
open System.IO.Pipes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

// ─────────────────────────────────────────────────────────────────────────────
//  Integration test: subscribe → notifyChanged → client receives notification
// ─────────────────────────────────────────────────────────────────────────────
//
// This test validates:
//  1. NotificationMethods.ResourceUpdatedNotification constant exists in SDK 1.2.0
//  2. ResourceUpdatedNotificationParams has a Uri field
//  3. The full wire format works end-to-end over in-memory pipes
//  4. ResourceSubscriptions.notifyChanged dispatches via McpServer.SendNotificationAsync
//  5. SDK client SubscribeToResourceAsync receives the notification

// ─────────────────────────────────────────────────────────────────────────────
//  GatedWriteStream
//  A stream wrapper whose WriteAsync delay can be activated after construction.
//  During MCP initialization the gate is open (zero delay) so handshake messages
//  flow at full speed. After setup completes, EnableThrottle is called, and every
//  subsequent WriteAsync sleeps for `writeDelay` before writing.  This makes
//  each session's SendNotificationAsync observable at the client arrival-time
//  level without slowing down the initialization phase.
// ─────────────────────────────────────────────────────────────────────────────
type GatedWriteStream(inner: Stream, writeDelay: TimeSpan) =
    inherit Stream()

    let mutable throttleEnabled = false

    member _.EnableThrottle() = throttleEnabled <- true

    override _.CanRead  = inner.CanRead
    override _.CanWrite = inner.CanWrite
    override _.CanSeek  = inner.CanSeek
    override _.Length   = inner.Length
    override _.Position with get () = inner.Position and set v = inner.Position <- v
    override _.Flush() = inner.Flush()
    override _.Read(buf, off, cnt) = inner.Read(buf, off, cnt)
    override _.Seek(off, origin) = inner.Seek(off, origin)
    override _.SetLength(v) = inner.SetLength(v)
    override _.Write(buf, off, cnt) = inner.Write(buf, off, cnt)

    override _.WriteAsync(buf: ReadOnlyMemory<byte>, ct: CancellationToken) : ValueTask =
        ValueTask(task {
            if throttleEnabled then do! Task.Delay(writeDelay, ct)
            do! inner.WriteAsync(buf, ct)
        })

    override _.WriteAsync(buf: byte[], off, cnt, ct) : Task =
        task {
            if throttleEnabled then do! Task.Delay(writeDelay, ct)
            do! inner.WriteAsync(buf, off, cnt, ct)
        }

    override _.FlushAsync(ct) = inner.FlushAsync(ct)

    override _.DisposeAsync() = inner.DisposeAsync()
    override _.Dispose(disposing) =
        if disposing then inner.Dispose()
        base.Dispose(disposing)

// ─────────────────────────────────────────────────────────────────────────────
//  Shared helper: build one server + client stack
// ─────────────────────────────────────────────────────────────────────────────

[<NoComparison; NoEquality>]
type ServerStack = {
    Services: ServiceProvider
    HostedServices: IHostedService list
    SdkClient: McpClient
    Subscription: IAsyncDisposable
    Registry: ResourceSubscriptionRegistry
    /// Enables the write delay on the server output stream.
    /// Call this on all stacks before firing notifyChanged so that
    /// the throttle only applies to notification writes, not initialization.
    EnableThrottle: unit -> unit
}

let private buildStack
    (config: ServerConfig)
    (uri: ResourceUri)
    (resourceUri: string)
    (writeDelay: TimeSpan)
    (arrivalBag: System.Collections.Concurrent.ConcurrentBag<DateTimeOffset>)
    (ct: CancellationToken)
    : Task<ServerStack> =
    task {
        let serverToClientPipeServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)
        let serverToClientPipeClient = new AnonymousPipeClientStream(PipeDirection.In, serverToClientPipeServer.ClientSafePipeHandle)
        let clientToServerPipeServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)
        let clientToServerPipeClient = new AnonymousPipeClientStream(PipeDirection.In, clientToServerPipeServer.ClientSafePipeHandle)

        let serverInputStream : Stream = clientToServerPipeClient  // server reads from client-side pipe
        let rawServerOutputStream : Stream = serverToClientPipeServer
        // Wrap server output in a GatedWriteStream. The throttle is disabled during
        // initialization (so handshake writes are fast) and enabled by the caller
        // just before notifyChanged fires.  This makes parallel vs serial dispatch
        // observable at the client arrival-time level without slowing down setup.
        let gatedStream = new GatedWriteStream(rawServerOutputStream, writeDelay)
        let serverOutputStream : Stream = gatedStream

        let clientOutputStream : Stream = clientToServerPipeServer  // client writes to server
        let clientInputStream : Stream = serverToClientPipeClient   // client reads from server

        let services = ServiceCollection()
        services.AddLogging(fun l -> l.SetMinimumLevel(LogLevel.Critical) |> ignore) |> ignore
        let mcpBuilder = services.AddMcpServer()
        mcpBuilder.WithStreamServerTransport(serverInputStream, serverOutputStream) |> ignore

        let registry =
            match Server.registerAllInternal mcpBuilder config with
            | Some r -> r
            | None -> failtest "registry must be Some when config has resources"

        let sp = services.BuildServiceProvider()
        let hostedServices = sp.GetServices<IHostedService>() |> Seq.toList
        for svc in hostedServices do
            do! svc.StartAsync(ct)

        do! Task.Delay(200, ct)

        let clientTransport = StreamClientTransport(clientOutputStream, clientInputStream)
        let clientOptions = McpClientOptions()
        clientOptions.ClientInfo <- Implementation(Name = "parallel-test-client", Version = "1.0.0")
        let! sdkClient = ModelContextProtocol.Client.McpClient.CreateAsync(clientTransport, clientOptions)

        // Subscribe: record arrival time when notification received
        let! subscription =
            sdkClient.SubscribeToResourceAsync(
                resourceUri,
                Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>(fun _p _ct2 ->
                    arrivalBag.Add(DateTimeOffset.UtcNow)
                    ValueTask.CompletedTask),
                null,
                ct)

        // Give the server time to process the subscribe request
        do! Task.Delay(200, ct)

        return {
            Services = sp
            HostedServices = hostedServices
            SdkClient = sdkClient
            Subscription = subscription
            Registry = registry
            EnableThrottle = fun () -> gatedStream.EnableThrottle()
        }
    }

let private disposeStack (stack: ServerStack) : Task<unit> =
    task {
        do! stack.Subscription.DisposeAsync()
        do! stack.SdkClient.DisposeAsync()
        for svc in stack.HostedServices do
            do! svc.StopAsync(CancellationToken.None)
        do! stack.Services.DisposeAsync()
    }

[<Tests>]
let subscribeIntegrationTests =
    testList "Subscribe end-to-end integration" [
        testCase "B4: subscribe → notifyChanged → client receives notification within 5s" <| fun _ ->
            let run () =
                task {
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(20.0))
                    let resourceUri = "https://example.com/data"

                    // Anonymous pipes for bidirectional in-memory communication.
                    // serverToClient: server writes → client reads
                    // clientToServer: client writes → server reads
                    use serverToClientServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)
                    use serverToClientClient = new AnonymousPipeClientStream(PipeDirection.In, serverToClientServer.ClientSafePipeHandle)
                    use clientToServerClient = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)
                    use clientToServerServer = new AnonymousPipeClientStream(PipeDirection.In, clientToServerClient.ClientSafePipeHandle)

                    let serverInputStream : Stream = clientToServerServer  // server reads client output
                    let serverOutputStream : Stream = serverToClientServer  // server writes to client

                    // NOTE: StreamClientTransport takes (writeStream, readStream), NOT (readStream, writeStream)
                    let clientOutputStream : Stream = clientToServerClient  // client writes to server
                    let clientInputStream : Stream = serverToClientClient   // client reads from server

                    // Build the FsMcp ServerConfig
                    let uri = ResourceUri.create resourceUri |> Result.defaultWith (fun e -> failtest $"URI: %A{e}")
                    let resourceDef = {
                        Uri = uri
                        Name = "data"
                        Description = None
                        MimeType = None
                        Handler = fun _ -> task {
                            return Ok (TextResource (uri, MimeType.create "text/plain" |> Result.defaultWith (fun e -> failtest $"%A{e}"), "hello"))
                        }
                    }
                    let config =
                        mcpServer {
                            name "IntegTest"
                            version "1.0"
                            resource resourceDef
                            useStdio
                        }

                    // Build server DI container and register subscription handlers
                    let services = ServiceCollection()
                    services.AddLogging(fun l -> l.SetMinimumLevel(LogLevel.Critical) |> ignore) |> ignore
                    let mcpBuilder = services.AddMcpServer()
                    mcpBuilder.WithStreamServerTransport(serverInputStream, serverOutputStream) |> ignore

                    // Register resources + subscribe handlers, capture registry
                    let registry = Server.registerAllInternal mcpBuilder config

                    // Build and start hosted services
                    let sp = services.BuildServiceProvider()
                    let hostedServices = sp.GetServices<IHostedService>() |> Seq.toList
                    for svc in hostedServices do
                        do! svc.StartAsync(cts.Token)

                    // Allow server to initialize
                    do! Task.Delay(200)

                    // Connect SDK client (args: writeStream, readStream)
                    let clientTransport = StreamClientTransport(clientOutputStream, clientInputStream)
                    let clientOptions = McpClientOptions()
                    clientOptions.ClientInfo <- Implementation(Name = "test-client", Version = "1.0.0")
                    let! sdkClient = ModelContextProtocol.Client.McpClient.CreateAsync(clientTransport, clientOptions)

                    // Register notification subscription with a completion source
                    let notified = TaskCompletionSource<string>()

                    let! subscription =
                        sdkClient.SubscribeToResourceAsync(
                            resourceUri,
                            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>(fun p _ct ->
                                notified.TrySetResult(p.Uri) |> ignore
                                ValueTask.CompletedTask),
                            null,
                            cts.Token)

                    // Wait for subscribe to be processed
                    do! Task.Delay(200)

                    // Fire notifyChanged through the registry
                    match registry with
                    | Some reg ->
                        // Registry should have a subscriber from the client's subscribe call
                        Expect.isTrue (reg.Subscribers.Count > 0) "registry has subscriber after subscribe"

                        do! ResourceSubscriptions.notifyChanged uri reg

                        // Assert notification arrives within 5s
                        let! winner = Task.WhenAny(notified.Task, Task.Delay(5000))
                        Expect.isTrue (winner = (notified.Task :> Task)) "notification arrived within 5s"
                        Expect.equal notified.Task.Result resourceUri "received URI matches subscribed URI"

                    | None ->
                        failtest "registry should not be None when server has resources"

                    // Cleanup
                    do! subscription.DisposeAsync()
                    do! sdkClient.DisposeAsync()
                    for svc in hostedServices do
                        do! svc.StopAsync(CancellationToken.None)
                    do! sp.DisposeAsync()
                }

            run () |> Async.AwaitTask |> Async.RunSynchronously

        // ─────────────────────────────────────────────────────────────────────
        //  B5: parallel fan-out test
        //
        //  Contract: notifyChanged dispatches to all subscribed sessions
        //  CONCURRENTLY, not serially.
        //
        //  Setup: 3 independent server/client stacks all share one merged
        //  ResourceSubscriptionRegistry (their SessionServers entries are
        //  combined after all clients have subscribed). Each server's output
        //  stream is wrapped in a ThrottledWriteStream that adds 150ms per
        //  write. This turns the parallel/serial distinction into a measurable
        //  arrival-time window:
        //
        //    parallel  → all 3 SendNotificationAsync calls start at t≈0,
        //                each takes ~150ms → all 3 clients receive at t≈150ms
        //                → arrival window ≈ 10-30ms  << 200ms threshold (PASS)
        //
        //    serial    → session 1 at t=0→150ms, session 2 at t=150→300ms,
        //                session 3 at t=300→450ms
        //                → arrival window ≈ 300ms     >> 200ms threshold (FAIL)
        //
        //  The 200ms threshold is generous relative to the ~30ms scheduling
        //  jitter of the SDK/pipe path, yet tight enough to catch the 300ms
        //  serial gap.
        // ─────────────────────────────────────────────────────────────────────
        testCase "B5: notifyChanged dispatches to 3 sessions in parallel (arrival window < 200ms)" <| fun _ ->
            let run () =
                task {
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                    let resourceUri = "https://example.com/parallel-data"
                    let uri = ResourceUri.create resourceUri |> Result.defaultWith (fun e -> failtest $"URI: %A{e}")

                    let resourceDef = {
                        Uri = uri
                        Name = "parallel-data"
                        Description = None
                        MimeType = None
                        Handler = fun _ -> task {
                            return Ok (TextResource (uri, MimeType.create "text/plain" |> Result.defaultWith (fun e -> failtest $"%A{e}"), "hello"))
                        }
                    }
                    let config =
                        mcpServer {
                            name "ParallelIntegTest"
                            version "1.0"
                            resource resourceDef
                            useStdio
                        }

                    // Each notification write will be throttled by 150ms (activated
                    // after setup completes — see GatedWriteStream). Without throttling,
                    // pipe writes finish in microseconds and parallel/serial are
                    // indistinguishable at the arrival-time level.
                    let writeDelay = TimeSpan.FromMilliseconds(150.0)

                    let arrivals = System.Collections.Concurrent.ConcurrentBag<DateTimeOffset>()

                    // Build 3 independent server/client stacks.
                    // Each stack has its own pipe pair, DI container, and registry.
                    // Throttle is disabled during initialization so handshakes are fast.
                    let! stack1 = buildStack config uri resourceUri writeDelay arrivals cts.Token
                    let! stack2 = buildStack config uri resourceUri writeDelay arrivals cts.Token
                    let! stack3 = buildStack config uri resourceUri writeDelay arrivals cts.Token

                    // WithStreamServerTransport assigns the same sessionId (often "")
                    // to all single-session servers. To fan out to all 3 real McpServer
                    // instances we build a fresh merged registry with 3 DISTINCT synthetic
                    // session IDs ("stack-1", "stack-2", "stack-3"), each mapped to the
                    // real McpServer that can reach its respective client.
                    //
                    // This mirrors what the production subscribe handler does: it stores
                    // (sessionId → McpServer) in SessionServers and registers a Subscriber
                    // entry. notifyChanged then fans out to each distinct session.
                    let mergedReg = ResourceSubscriptions.create ()

                    let syntheticSessions = [| "stack-1"; "stack-2"; "stack-3" |]
                    let stacks = [| stack1; stack2; stack3 |]
                    for i in 0..2 do
                        let sid = syntheticSessions[i]
                        let stackReg = stacks[i].Registry
                        // Pick the McpServer that was registered for this stack's real session
                        match stackReg.SessionServers |> Seq.tryHead with
                        | None ->
                            failtest $"stack {i+1} registry has no SessionServer entry after subscribe"
                        | Some kv ->
                            mergedReg.SessionServers.[sid] <- kv.Value
                        // Add a subscriber entry for the synthetic session ID
                        ResourceSubscriptions.subscribe sid uri mergedReg |> ignore

                    // All 3 sessions must be subscribed to the same URI
                    Expect.equal mergedReg.Subscribers.Count 3 "merged registry has exactly 3 subscribers"
                    Expect.equal mergedReg.SessionServers.Count 3 "merged registry has exactly 3 session servers"

                    // Enable write throttle on all 3 stacks BEFORE notifyChanged.
                    // From this point on, every server-output write takes ~150ms,
                    // making parallel vs serial timing observable at the clients.
                    stack1.EnableThrottle()
                    stack2.EnableThrottle()
                    stack3.EnableThrottle()

                    // Dispatch: production notifyChanged fans out in parallel
                    do! ResourceSubscriptions.notifyChanged uri mergedReg

                    // Wait for all 3 notifications to arrive (generous 10s timeout).
                    // Use a completion-source polled via Task.WhenAny to avoid
                    // a let-rec inside task{} (which triggers FS3511).
                    let allArrived = TaskCompletionSource<unit>()
                    let mutable waitIter = 0
                    while not allArrived.Task.IsCompleted do
                        do! Task.Delay(50, cts.Token)
                        waitIter <- waitIter + 1
                        if arrivals.Count >= 3 then
                            allArrived.TrySetResult(()) |> ignore
                        elif waitIter > 200 then // 200 × 50ms = 10s
                            failtest $"only {arrivals.Count}/3 notifications arrived within 10s"

                    // Arrival-time window: all 3 notifications should arrive within
                    // 200ms of each other. Parallel dispatch achieves this because all
                    // 3 writes start simultaneously (window ≈ 30ms). Serial dispatch
                    // would space arrivals by ~150ms each (window ≈ 300ms).
                    let times = arrivals |> Seq.map _.ToUnixTimeMilliseconds() |> Seq.toArray
                    let windowMs = (Array.max times) - (Array.min times)
                    Expect.isLessThan windowMs 200L
                        $"all 3 notifications arrived within 200ms (actual window: {windowMs}ms) — proves parallel dispatch"

                    // Cleanup all 3 stacks
                    do! disposeStack stack1
                    do! disposeStack stack2
                    do! disposeStack stack3
                }

            run () |> Async.AwaitTask |> Async.RunSynchronously
    ]
