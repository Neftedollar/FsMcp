module FsMcp.Server.Tests.SubscriptionsTests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open FsMcp.Core.Validation
open FsMcp.Server

let private uri =
    ResourceUri.create "https://example.com/resource"
    |> Result.defaultWith (fun error -> failtest $"Invalid URI: %A{error}")

let private otherUri =
    ResourceUri.create "https://example.com/other"
    |> Result.defaultWith (fun error -> failtest $"Invalid URI: %A{error}")

let private serverWithSend (sessionId: string) (send: CancellationToken -> Task) =
    let messages = Channel.CreateUnbounded<JsonRpcMessage>()
    let transport =
        { new ITransport with
            member _.SessionId = sessionId
            member _.MessageReader = messages.Reader
            member _.SendMessageAsync(_, cancellationToken) = send cancellationToken
            member _.DisposeAsync() = ValueTask.CompletedTask }
    let options = McpServerOptions()
    options.ServerInfo <- Implementation(Name = "subscription-test", Version = "2.0")
    let services = ServiceCollection().BuildServiceProvider()
    McpServer.Create(transport, options, NullLoggerFactory.Instance, services)

[<Tests>]
let subscriptionsTests =
    testList "ResourceSubscriptions" [
        testCase "create is empty and uses a bounded default" <| fun _ ->
            let registry = ResourceSubscriptions.create ()
            Expect.isTrue (ResourceSubscriptions.isEmpty registry) "new registry is empty"
            Expect.equal
                registry.MaxSubscriptionsPerSession
                ResourceSubscriptions.DefaultMaxSubscriptionsPerSession
                "default bound"

        testCase "createWithLimit rejects non-positive bounds" <| fun _ ->
            for value in [ 0; -1 ] do
                Expect.throwsT<ArgumentException>
                    (fun () -> ResourceSubscriptions.createWithLimit value |> ignore)
                    "the registry must stay bounded"

        testCase "missing session identity fails without retaining a server" <| fun _ ->
            let registry = ResourceSubscriptions.create ()
            match ResourceSubscriptions.subscribe "" null uri registry with
            | Error MissingSessionId -> ()
            | other -> failtest $"unexpected result: %A{other}"
            Expect.isTrue (ResourceSubscriptions.isEmpty registry) "invalid subscription leaves no state"

        testCase "duplicate subscribe is idempotent and unsubscribe cleans session state" <| fun _ ->
            let server = serverWithSend "session-1" (fun _ -> Task.CompletedTask)
            let registry = ResourceSubscriptions.createWithLimit 2
            let first =
                ResourceSubscriptions.subscribe "session-1" server uri registry
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
            let second =
                ResourceSubscriptions.subscribe "session-1" server uri registry
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
            Expect.equal first second "same session and URI share a subscription"
            Expect.equal (ResourceSubscriptions.subscriptionCount registry) 1 "one subscription"
            Expect.equal (ResourceSubscriptions.trackedSessionCount registry) 1 "one server retained"
            ResourceSubscriptions.unsubscribe first registry
            Expect.isTrue (ResourceSubscriptions.isEmpty registry) "last unsubscribe purges the session server"

        testCase "per-session bounds reject atomically" <| fun _ ->
            let server = serverWithSend "bounded" (fun _ -> Task.CompletedTask)
            let registry = ResourceSubscriptions.createWithLimit 1
            ResourceSubscriptions.subscribe "bounded" server uri registry
            |> Result.defaultWith (fun error -> failtest $"%A{error}")
            |> ignore
            match ResourceSubscriptions.subscribe "bounded" server otherUri registry with
            | Error(SubscriptionLimitExceeded 1) -> ()
            | other -> failtest $"unexpected limit result: %A{other}"
            Expect.equal (ResourceSubscriptions.subscriptionCount registry) 1 "failed subscribe is mutation-free"

        testCase "subscription bounds are independent per session" <| fun _ ->
            let firstServer = serverWithSend "first" (fun _ -> Task.CompletedTask)
            let secondServer = serverWithSend "second" (fun _ -> Task.CompletedTask)
            let registry = ResourceSubscriptions.createWithLimit 1
            ResourceSubscriptions.subscribe "first" firstServer uri registry
            |> Result.defaultWith (fun error -> failtest $"%A{error}")
            |> ignore
            ResourceSubscriptions.subscribe "second" secondServer uri registry
            |> Result.defaultWith (fun error -> failtest $"%A{error}")
            |> ignore
            Expect.equal (ResourceSubscriptions.subscriptionCount registry) 2 "each session gets its own bound"
            Expect.equal (ResourceSubscriptions.trackedSessionCount registry) 2 "both servers are retained"

        testCase "unsubscribeResource retains a session until its last resource is removed" <| fun _ ->
            let server = serverWithSend "multi-resource" (fun _ -> Task.CompletedTask)
            let registry = ResourceSubscriptions.createWithLimit 2
            for resource in [ uri; otherUri ] do
                ResourceSubscriptions.subscribe "multi-resource" server resource registry
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
                |> ignore
            ResourceSubscriptions.unsubscribeResource "multi-resource" uri registry
            Expect.equal (ResourceSubscriptions.subscriptionCount registry) 1 "other resource remains"
            Expect.equal (ResourceSubscriptions.trackedSessionCount registry) 1 "session still has work"
            ResourceSubscriptions.unsubscribeResource "multi-resource" otherUri registry
            Expect.isTrue (ResourceSubscriptions.isEmpty registry) "last resource purges the server"

        testCase "failed notification removes only the stale session" <| fun _ ->
            let healthy = serverWithSend "healthy" (fun _ -> Task.CompletedTask)
            let stale =
                serverWithSend "stale" (fun _ ->
                    Task.FromException(InvalidOperationException("disconnected")))
            let registry = ResourceSubscriptions.create ()
            for sessionId, server in [ "healthy", healthy; "stale", stale ] do
                ResourceSubscriptions.subscribe sessionId server uri registry
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
                |> ignore
            ResourceSubscriptions.notifyChanged uri registry
            |> fun pending -> pending.GetAwaiter().GetResult()
            Expect.equal (ResourceSubscriptions.subscriptionCount registry) 1 "stale entry is removed"
            Expect.equal (ResourceSubscriptions.trackedSessionIds registry) [ "healthy" ] "healthy target remains"

        testCase "a transport that ignores cancellation is timed out and purged" <| fun _ ->
            let run () = task {
                let sendStarted =
                    TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                let releaseSend =
                    TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                let server =
                    serverWithSend "blocked-session" (fun _ ->
                        sendStarted.TrySetResult() |> ignore
                        releaseSend.Task)
                let registry = ResourceSubscriptions.create ()
                ResourceSubscriptions.subscribe "blocked-session" server uri registry
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
                |> ignore

                try
                    let notification =
                        registry
                        |> ResourceSubscriptions.notifyChangedWithTimeoutInternal
                            (TimeSpan.FromMilliseconds 50.0)
                            uri
                            CancellationToken.None
                    do! sendStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                    do! notification.WaitAsync(TimeSpan.FromSeconds 5.0)
                    Expect.isTrue (ResourceSubscriptions.isEmpty registry) "timed-out server is purged"
                finally
                    releaseSend.TrySetResult() |> ignore
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously

        testCase "notifyChanged preserves caller cancellation" <| fun _ ->
            let run () = task {
                let sendStarted =
                    TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                let releaseSend =
                    TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                let server =
                    serverWithSend "cancelled-session" (fun _ ->
                        sendStarted.TrySetResult() |> ignore
                        releaseSend.Task)
                let registry = ResourceSubscriptions.create ()
                ResourceSubscriptions.subscribe "cancelled-session" server uri registry
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
                |> ignore
                use callerCancellation = new CancellationTokenSource()

                try
                    let notification =
                        registry
                        |> ResourceSubscriptions.notifyChangedWithCancellation
                            uri
                            callerCancellation.Token
                    do! sendStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                    callerCancellation.Cancel()
                    let mutable observedToken = CancellationToken.None
                    try
                        do! notification
                    with :? OperationCanceledException as error ->
                        observedToken <- error.CancellationToken
                    Expect.equal observedToken callerCancellation.Token "caller token is preserved"
                    Expect.equal
                        (ResourceSubscriptions.subscriptionCount registry)
                        1
                        "caller cancellation is not stale transport cleanup"
                finally
                    ResourceSubscriptions.unsubscribeAllForSession "cancelled-session" registry
                    releaseSend.TrySetResult() |> ignore
            }
            run () |> Async.AwaitTask |> Async.RunSynchronously
    ]
