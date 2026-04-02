# FsMcp.Server — Public API Contract

## Module: FsMcp.Server.Builder

```fsharp
/// Computation expression for building MCP servers
val mcpServer : McpServerBuilder

/// McpServerBuilder CE keywords:
///   name        : string -> unit
///   version     : string -> unit
///   tool        : ToolDefinition -> unit
///   resource    : ResourceDefinition -> unit
///   prompt      : PromptDefinition -> unit
///   middleware  : McpMiddleware -> unit
///   useStdio    : unit -> unit
///   useHttp     : string option -> unit

/// Build and run the configured server
val run      : ServerConfig -> Task<unit>
val runAsync : ServerConfig -> Async<unit>
```

## Module: FsMcp.Server.Tool

```fsharp
/// Convenience builder for tool definitions
val define : string -> string -> (Map<string, JsonElement> -> Task<Result<Content list, McpError>>) -> Result<ToolDefinition, ValidationError>
```

## Module: FsMcp.Server.Resource

```fsharp
/// Convenience builder for resource definitions
val define : string -> string -> (Map<string, string> -> Task<Result<ResourceContents, McpError>>) -> Result<ResourceDefinition, ValidationError>
```

## Module: FsMcp.Server.Prompt

```fsharp
/// Convenience builder for prompt definitions
val define : string -> PromptArgument list -> (Map<string, string> -> Task<Result<McpMessage list, McpError>>) -> Result<PromptDefinition, ValidationError>
```

## Module: FsMcp.Server.Middleware

```fsharp
/// Middleware type alias
type McpMiddleware =
    McpContext -> (McpContext -> Task<McpResponse>) -> Task<McpResponse>

/// Compose two middleware functions
val compose : McpMiddleware -> McpMiddleware -> McpMiddleware

/// Compose a list of middleware
val pipeline : McpMiddleware list -> McpMiddleware
```
