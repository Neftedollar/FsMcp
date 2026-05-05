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
    ]
