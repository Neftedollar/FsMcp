namespace FsMcp.Server

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server

// ─────────────────────────────────────────────────────────────────────────────
//  Resource subscription infrastructure
// ─────────────────────────────────────────────────────────────────────────────

/// Opaque identifier for a single subscription entry.
type SubscriptionId = SubscriptionId of Guid

/// A single subscription: maps (session, uri) to an id.
type SubscriptionEntry = {
    Id: SubscriptionId
    SessionId: string
    Uri: ResourceUri
}

/// Per-session server reference, used to send notifications.
/// SDK 1.2.0 does not expose a session-disconnect hook.
/// MUST be called on disconnect; see unsubscribeAllForSession.
[<NoComparison; NoEquality>]
type ResourceSubscriptionRegistry = {
    /// All active subscriptions, keyed by SubscriptionId.
    Subscribers: ConcurrentDictionary<SubscriptionId, SubscriptionEntry>
    /// Per-session McpServer references, for sending notifications.
    /// Entries are added on subscribe and removed on unsubscribeAllForSession.
    /// NOTE: SDK 1.2.0 does not expose a disconnect hook so cleanup is
    /// best-effort; see GitHub issue linked on unsubscribeAllForSession.
    SessionServers: ConcurrentDictionary<string, McpServer>
}

/// Functions for managing resource subscriptions and notifying clients.
module ResourceSubscriptions =

    /// Create a new, empty registry.
    let create () : ResourceSubscriptionRegistry = {
        Subscribers = ConcurrentDictionary<SubscriptionId, SubscriptionEntry>()
        SessionServers = ConcurrentDictionary<string, McpServer>()
    }

    /// Subscribe a session to a resource URI.
    /// Idempotent under sequential calls: if the same (sessionId, uri) already exists,
    /// returns the existing SubscriptionId. Two concurrent subscribes for the same
    /// (sessionId, uri) may transiently produce two entries (the tryFind/insert pair
    /// is not atomic across keys); both will be cleaned up by unsubscribeAllForSession,
    /// and notifyChanged de-dupes by session via Seq.distinct, so the only cost is
    /// one extra dictionary entry until disconnect.
    let subscribe (sessionId: string) (uri: ResourceUri) (reg: ResourceSubscriptionRegistry) : SubscriptionId =
        let existing =
            reg.Subscribers
            |> Seq.tryFind (fun kv -> kv.Value.SessionId = sessionId && kv.Value.Uri = uri)
        match existing with
        | Some kv -> kv.Value.Id
        | None ->
            let id = SubscriptionId (Guid.NewGuid())
            let entry = { Id = id; SessionId = sessionId; Uri = uri }
            reg.Subscribers.[id] <- entry
            id

    /// Unsubscribe by id.
    let unsubscribe (id: SubscriptionId) (reg: ResourceSubscriptionRegistry) : unit =
        reg.Subscribers.TryRemove(id) |> ignore

    /// Unsubscribe all subscriptions for a session and remove its server reference.
    /// MUST be called by transport on disconnect.
    /// SDK 1.2.0 does not expose a session-disconnect hook — once the SDK adds
    /// IHostedMcpSessionLifecycle or equivalent, wire this call there.
    /// See: https://github.com/Neftedollar/FsMcp/issues/5
    let unsubscribeAllForSession (sessionId: string) (reg: ResourceSubscriptionRegistry) : unit =
        let toRemove =
            reg.Subscribers
            |> Seq.filter (fun kv -> kv.Value.SessionId = sessionId)
            |> Seq.map (fun kv -> kv.Key)
            |> Seq.toList
        for id in toRemove do
            reg.Subscribers.TryRemove(id) |> ignore
        reg.SessionServers.TryRemove(sessionId) |> ignore

    /// Send notifications/resources/updated to all sessions subscribed to the given uri.
    /// Fan-out is parallel: one slow or disconnected client does not block others.
    /// Best-effort: per-session failures are caught and swallowed (a disconnected
    /// client should not break notification dispatch for the rest), so Task.WhenAll
    /// here will not throw under normal operation.
    let notifyChanged (uri: ResourceUri) (reg: ResourceSubscriptionRegistry) : Task<unit> =
        task {
            // Collect distinct session IDs subscribed to this URI
            let sessions =
                reg.Subscribers
                |> Seq.filter (fun kv -> kv.Value.Uri = uri)
                |> Seq.map (fun kv -> kv.Value.SessionId)
                |> Seq.distinct
                |> Seq.toList

            let notifyForSession (sessionId: string) : Task =
                task {
                    match reg.SessionServers.TryGetValue(sessionId) with
                    | true, server ->
                        try
                            do! server.SendNotificationAsync(
                                    NotificationMethods.ResourceUpdatedNotification,
                                    ResourceUpdatedNotificationParams(Uri = ResourceUri.value uri))
                        with _ -> () // Swallow: client may have disconnected
                    | _ -> () // Session no longer tracked — silently skip
                } :> Task

            // Parallel fan-out (B5)
            let pendings =
                sessions
                |> List.map notifyForSession
                |> List.toArray
            do! Task.WhenAll(pendings)
        }
