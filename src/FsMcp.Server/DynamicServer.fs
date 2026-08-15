namespace FsMcp.Server

open FsMcp.Core
open FsMcp.Core.Validation

/// Retained only as an opaque migration marker. FsMcp 1.x dynamic registrations
/// never updated the live SDK registry, so instances cannot be constructed in 2.0.
[<Sealed>]
type DynamicServerConfig private () = class end

/// Functions for managing the legacy dynamic server surface.
module DynamicServer =
    let private unavailableMessage =
        "DynamicServer changed only an in-memory ServerConfig in FsMcp 1.x; live SDK registrations never observed those mutations. The deceptive behavior was removed in 2.0. Rebuild and replace the composed server registration instead."

    [<System.Obsolete("DynamicServer did not update a live MCP server and now fails closed. Rebuild and replace the composed server registration.")>]
    let create (config: ServerConfig) : DynamicServerConfig =
        ignore config
        raise (FsMcpConfigException unavailableMessage)

    [<System.Obsolete("DynamicServer did not update a live MCP server and now fails closed. Rebuild and replace the composed server registration.")>]
    let addTool (tool: ToolDefinition) (server: DynamicServerConfig) =
        ignore tool
        ignore server
        raise (FsMcpConfigException unavailableMessage)

    [<System.Obsolete("DynamicServer did not update a live MCP server and now fails closed. Rebuild and replace the composed server registration.")>]
    let removeTool (name: ToolName) (server: DynamicServerConfig) =
        ignore name
        ignore server
        raise (FsMcpConfigException unavailableMessage)

    [<System.Obsolete("DynamicServer did not update a live MCP server and now fails closed. Rebuild and replace the composed server registration.")>]
    let toolCount (server: DynamicServerConfig) : int =
        ignore server
        raise (FsMcpConfigException unavailableMessage)

    [<System.Obsolete("DynamicServer did not update a live MCP server and now fails closed. Rebuild and replace the composed server registration.")>]
    let subscribeToolsChanged (handler: unit -> unit) (server: DynamicServerConfig) : System.IDisposable =
        ignore handler
        ignore server
        raise (FsMcpConfigException unavailableMessage)

    /// Retained only as a fail-closed migration surface; no live change event exists.
    [<System.Obsolete("DynamicServer did not update a live MCP server and now fails closed. Rebuild and replace the composed server registration.")>]
    let onToolsChanged (server: DynamicServerConfig) : IEvent<unit> =
        ignore server
        raise (FsMcpConfigException unavailableMessage)
