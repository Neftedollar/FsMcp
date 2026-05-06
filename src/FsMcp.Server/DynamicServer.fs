namespace FsMcp.Server

open FsMcp.Core
open FsMcp.Core.Validation

/// A mutable server configuration that supports adding/removing tools at runtime.
[<NoComparison; NoEquality>]
type DynamicServerConfig = {
    mutable Config: ServerConfig
    OnToolsChanged: Event<unit>
}

/// Functions for managing a dynamic server with hot-reload tool support.
module DynamicServer =
    /// Create a dynamic server from an initial config.
    let create (config: ServerConfig) : DynamicServerConfig =
        { Config = config; OnToolsChanged = Event<unit>() }

    /// Add a tool at runtime. Fails if a tool with the same name already exists.
    let addTool (tool: ToolDefinition) (server: DynamicServerConfig) =
        let newTools = server.Config.Tools @ [tool]
        // Validate no duplicates
        let toolNames = newTools |> List.map (fun t -> ToolName.value t.Name)
        let duplicate =
            toolNames
            |> List.groupBy id
            |> List.tryFind (fun (_, group) -> List.length group > 1)
        match duplicate with
        | Some (name, _) ->
            raise (FsMcpConfigException $"Cannot add tool: a tool named '{name}' already exists. Remove it first with DynamicServer.removeTool.")
        | None ->
            server.Config <- { server.Config with Tools = newTools }
            server.OnToolsChanged.Trigger()

    /// Remove a tool by name at runtime.
    let removeTool (name: ToolName) (server: DynamicServerConfig) =
        let newTools = server.Config.Tools |> List.filter (fun t -> t.Name <> name)
        server.Config <- { server.Config with Tools = newTools }
        server.OnToolsChanged.Trigger()

    /// Get current tool count.
    let toolCount (server: DynamicServerConfig) = List.length server.Config.Tools

    /// Subscribe to tool list changes. Returns an IDisposable; caller must dispose
    /// when the subscriber's owner goes out of scope to avoid event-handler retention.
    let subscribeToolsChanged (handler: unit -> unit) (server: DynamicServerConfig) : System.IDisposable =
        server.OnToolsChanged.Publish |> Observable.subscribe (fun () -> handler ())

    /// Subscribe to tool list changes.
    [<System.Obsolete("Use subscribeToolsChanged for a disposable subscription. The IEvent.Add returned by this function does not support unsubscription.")>]
    let onToolsChanged (server: DynamicServerConfig) = server.OnToolsChanged.Publish
