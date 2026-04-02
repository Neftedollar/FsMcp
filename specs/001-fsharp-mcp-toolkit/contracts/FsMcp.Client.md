# FsMcp.Client — Public API Contract

## Module: FsMcp.Client

```fsharp
/// Create a client connected to an MCP server
val connect : ClientConfig -> Task<McpClient>

/// List available tools
val listTools : McpClient -> Task<ToolDefinition list>

/// Call a tool by name with arguments
val callTool : McpClient -> ToolName -> Map<string, obj> -> Task<Result<Content list, McpError>>

/// List available resources
val listResources : McpClient -> Task<ResourceDefinition list>

/// Read a resource by URI
val readResource : McpClient -> ResourceUri -> Task<Result<ResourceContents, McpError>>

/// List available prompts
val listPrompts : McpClient -> Task<PromptDefinition list>

/// Get a prompt with arguments
val getPrompt : McpClient -> PromptName -> Map<string, string> -> Task<Result<McpMessage list, McpError>>

/// Disconnect and dispose
val disconnect : McpClient -> Task<unit>
```

## Module: FsMcp.Client.Transport

```fsharp
/// Create a stdio transport config (launches server as child process)
val stdio : command: string -> args: string list -> ClientTransport

/// Create an HTTP transport config
val http : uri: string -> ClientTransport

/// Create an HTTP transport with auth headers
val httpWithHeaders : uri: string -> headers: Map<string, string> -> ClientTransport
```

## Module: FsMcp.Client.Async

```fsharp
/// Async<'T> wrappers for all FsMcp.Client functions
val connect       : ClientConfig -> Async<McpClient>
val listTools     : McpClient -> Async<ToolDefinition list>
val callTool      : McpClient -> ToolName -> Map<string, obj> -> Async<Result<Content list, McpError>>
val listResources : McpClient -> Async<ResourceDefinition list>
val readResource  : McpClient -> ResourceUri -> Async<Result<ResourceContents, McpError>>
val listPrompts   : McpClient -> Async<PromptDefinition list>
val getPrompt     : McpClient -> PromptName -> Map<string, string> -> Async<Result<McpMessage list, McpError>>
val disconnect    : McpClient -> Async<unit>
```
