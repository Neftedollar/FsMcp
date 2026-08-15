namespace FsMcp.Server

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open FsMcp.Core.Validation
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server

/// Opaque identifier for a single subscription entry.
type SubscriptionId = private SubscriptionId of Guid

/// A single subscription: maps (session, uri) to an id.
type internal SubscriptionEntry = {
    Id: SubscriptionId
    SessionId: string
    Uri: ResourceUri
}

/// Why a resource subscription could not be registered.
type SubscriptionError =
    | MissingSessionId
    | SubscriptionLimitExceeded of maximum: int

/// Opaque, bounded per-session resource subscription state. Its mutable
/// dictionaries are intentionally hidden so callers cannot bypass the atomic
/// bound and cleanup invariants.
[<Sealed>]
type ResourceSubscriptionRegistry internal (
    subscribers: ConcurrentDictionary<SubscriptionId, SubscriptionEntry>,
    sessionServers: ConcurrentDictionary<string, McpServer>,
    maxSubscriptionsPerSession: int) =

    let gate = obj ()

    member internal _.Subscribers = subscribers
    member internal _.SessionServers = sessionServers
    member internal _.Gate = gate
    member _.MaxSubscriptionsPerSession = maxSubscriptionsPerSession

module ResourceSubscriptions =

    [<Literal>]
    let DefaultMaxSubscriptionsPerSession = 256

    let private defaultNotificationTimeout = TimeSpan.FromSeconds 30.0

    let createWithLimit (maxSubscriptionsPerSession: int) =
        if maxSubscriptionsPerSession <= 0 then
            invalidArg
                (nameof maxSubscriptionsPerSession)
                "The maximum number of subscriptions per session must be positive."

        ResourceSubscriptionRegistry(
            ConcurrentDictionary<SubscriptionId, SubscriptionEntry>(),
            ConcurrentDictionary<string, McpServer>(StringComparer.Ordinal),
            maxSubscriptionsPerSession)

    let create () = createWithLimit DefaultMaxSubscriptionsPerSession

    let subscriptionCount (reg: ResourceSubscriptionRegistry) =
        reg.Subscribers.Count

    let trackedSessionCount (reg: ResourceSubscriptionRegistry) =
        reg.SessionServers.Count

    let isEmpty reg = subscriptionCount reg = 0 && trackedSessionCount reg = 0

    let trackedSessionIds (reg: ResourceSubscriptionRegistry) =
        reg.SessionServers.Keys |> Seq.toList

    let subscribe
        (sessionId: string)
        (server: McpServer)
        (uri: ResourceUri)
        (reg: ResourceSubscriptionRegistry)
        : Result<SubscriptionId, SubscriptionError> =

        if String.IsNullOrWhiteSpace sessionId || isNull server then
            Error MissingSessionId
        else
            lock reg.Gate (fun () ->
                let existing =
                    reg.Subscribers
                    |> Seq.tryPick (fun keyValue ->
                        let entry = keyValue.Value
                        if entry.SessionId = sessionId && entry.Uri = uri then
                            Some entry.Id
                        else
                            None)

                match existing with
                | Some subscriptionId ->
                    reg.SessionServers.[sessionId] <- server
                    Ok subscriptionId
                | None ->
                    let sessionCount =
                        reg.Subscribers
                        |> Seq.sumBy (fun keyValue ->
                            if keyValue.Value.SessionId = sessionId then 1 else 0)

                    if sessionCount >= reg.MaxSubscriptionsPerSession then
                        Error(SubscriptionLimitExceeded reg.MaxSubscriptionsPerSession)
                    else
                        let id = SubscriptionId(Guid.NewGuid())
                        let entry = { Id = id; SessionId = sessionId; Uri = uri }
                        reg.Subscribers.[id] <- entry
                        reg.SessionServers.[sessionId] <- server
                        Ok id)

    let unsubscribe (id: SubscriptionId) (reg: ResourceSubscriptionRegistry) =
        lock reg.Gate (fun () ->
            match reg.Subscribers.TryRemove id with
            | true, entry ->
                let sessionStillSubscribed =
                    reg.Subscribers
                    |> Seq.exists (fun keyValue -> keyValue.Value.SessionId = entry.SessionId)
                if not sessionStillSubscribed then
                    reg.SessionServers.TryRemove entry.SessionId |> ignore
            | _ -> ())

    let unsubscribeResource
        (sessionId: string)
        (uri: ResourceUri)
        (reg: ResourceSubscriptionRegistry) =

        lock reg.Gate (fun () ->
            reg.Subscribers
            |> Seq.choose (fun keyValue ->
                let entry = keyValue.Value
                if entry.SessionId = sessionId && entry.Uri = uri then Some keyValue.Key else None)
            |> Seq.toArray
            |> Array.iter (fun id -> reg.Subscribers.TryRemove id |> ignore)

            let sessionStillSubscribed =
                reg.Subscribers
                |> Seq.exists (fun keyValue -> keyValue.Value.SessionId = sessionId)
            if not sessionStillSubscribed then
                reg.SessionServers.TryRemove sessionId |> ignore)

    let unsubscribeAllForSession
        (sessionId: string)
        (reg: ResourceSubscriptionRegistry) =

        lock reg.Gate (fun () ->
            reg.Subscribers
            |> Seq.choose (fun keyValue ->
                if keyValue.Value.SessionId = sessionId then Some keyValue.Key else None)
            |> Seq.toArray
            |> Array.iter (fun id -> reg.Subscribers.TryRemove id |> ignore)
            reg.SessionServers.TryRemove sessionId |> ignore)

    let private observeLateFault (pending: Task) =
        if not (isNull pending) && not pending.IsCompleted then
            pending.ContinueWith(
                (fun (completed: Task) -> ignore completed.Exception),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                ||| TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default)
            |> ignore

    let private notifyChangedCore
        (sendTimeout: TimeSpan)
        (uri: ResourceUri)
        (registry: ResourceSubscriptionRegistry)
        (cancellationToken: CancellationToken)
        : Task<unit> =

        if sendTimeout <= TimeSpan.Zero then
            invalidArg (nameof sendTimeout) "The notification send timeout must be positive."

        task {
            cancellationToken.ThrowIfCancellationRequested()

            let sessions =
                registry.Subscribers
                |> Seq.choose (fun keyValue ->
                    if keyValue.Value.Uri = uri then Some keyValue.Value.SessionId else None)
                |> Seq.distinct
                |> Seq.toArray

            let notifySession (sessionId: string) : Task =
                task {
                    match registry.SessionServers.TryGetValue sessionId with
                    | true, server ->
                        use sendCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                        sendCancellation.CancelAfter(sendTimeout)
                        let mutable pending: Task = null
                        try
                            pending <-
                                server.SendNotificationAsync(
                                    NotificationMethods.ResourceUpdatedNotification,
                                    ResourceUpdatedNotificationParams(Uri = ResourceUri.value uri),
                                    cancellationToken = sendCancellation.Token)
                            do! pending.WaitAsync(sendCancellation.Token)
                        with
                        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                            observeLateFault pending
                            return raise (OperationCanceledException(cancellationToken))
                        | _ ->
                            observeLateFault pending
                            unsubscribeAllForSession sessionId registry
                    | _ -> ()
                }

            try
                do! sessions |> Array.map notifySession |> Task.WhenAll
            with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException(cancellationToken))
        }

    /// Send notifications/resources/updated with cooperative caller cancellation.
    /// Fan-out is parallel and each session write has an independent 30-second
    /// bound. A failed or timed-out transport is removed; caller cancellation is
    /// rethrown with the caller's token. The registry is the final argument so the
    /// function composes naturally in an F# pipeline.
    let notifyChangedWithCancellation
        (uri: ResourceUri)
        (cancellationToken: CancellationToken)
        (reg: ResourceSubscriptionRegistry) =
        notifyChangedCore defaultNotificationTimeout uri reg cancellationToken

    let notifyChanged uri reg =
        notifyChangedWithCancellation uri CancellationToken.None reg

    let internal notifyChangedWithTimeoutInternal
        (sendTimeout: TimeSpan)
        (uri: ResourceUri)
        (cancellationToken: CancellationToken)
        (registry: ResourceSubscriptionRegistry) =
        notifyChangedCore sendTimeout uri registry cancellationToken
