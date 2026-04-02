# Research: F# MCP Toolkit

## R1: Microsoft ModelContextProtocol .NET SDK API Surface

**Decision**: Wrap the official `ModelContextProtocol` NuGet package.

**Rationale**: The SDK is the official C# implementation maintained under
`modelcontextprotocol/csharp-sdk`. It provides complete server and client
capabilities with stdio and HTTP/SSE transports. High reputation, actively
maintained, used by Microsoft tooling.

**Key SDK patterns to wrap**:

### Server-side
- `IServiceCollection.AddMcpServer()` → fluent builder returning
  `IMcpServerBuilder`
- `.WithStdioServerTransport()` / `.WithHttpTransport()` for transport
- `.WithTools<T>()` / `.WithToolsFromAssembly()` — attribute-based tool
  discovery via `[McpServerToolType]` and `[McpServerTool]`
- `.WithResources<T>()` — via `[McpServerResourceType]` and
  `[McpServerResource(UriTemplate = "...")]`
- `.WithPrompts<T>()` — via `[McpServerPromptType]` and `[McpServerPrompt]`
- Hosting via `Microsoft.Extensions.Hosting.Host`

### Client-side
- `McpClient.CreateAsync(transport)` → `IMcpClient`
- `client.ListToolsAsync()` → `IList<McpClientTool>`
- `client.CallToolAsync(name, args)` → `CallToolResult`
- `client.ListResourcesAsync()` → `IList<McpClientResource>`
- `client.ReadResourceAsync(uri)` → `ReadResourceResult`
- `client.ListPromptsAsync()` → `IList<McpClientPrompt>`
- `client.GetPromptAsync(name, args)` → `GetPromptResult`
- Transports: `StdioClientTransport`, `HttpClientTransport`

### Key C# SDK types to model as F# DUs
- `TextContentBlock`, `ImageContentBlock`, `EmbeddedResourceBlock` → `Content` DU
- `TextResourceContents`, `BlobResourceContents` → `ResourceContents` DU
- `ChatMessage`, `PromptMessage` → `McpMessage` DU
- `CallToolResult` (with `.IsError`, `.Content`) → `ToolCallResult` with Result semantics
- `Role` enum → preserve or map

**Alternatives considered**:
- Building directly on JSON-RPC: Rejected — violates Constitution Principle I
- Using a third-party F# MCP lib: None exist with sufficient maturity

---

## R2: F# DSL Design — Computation Expressions vs Builder Pattern

**Decision**: Use computation expressions (CEs) for the server builder DSL.

**Rationale**: CEs are the idiomatic F# way to build DSLs. They provide
`let!`-style syntax, custom keywords, and compose naturally. The CE wraps
the underlying `IMcpServerBuilder` fluent API.

**Design sketch**:
```fsharp
let server = mcpServer {
    name "My Server"
    version "1.0.0"
    
    tool "echo" {
        description "Echoes the message"
        handler (fun args -> task {
            let msg = args |> Arg.getString "message"
            return Content.text $"hello {msg}"
        })
    }
    
    resource "config://app/settings" {
        name "App Settings"
        mimeType "application/json"
        handler (fun () -> task {
            return Content.text """{"theme":"dark"}"""
        })
    }
    
    useStdio
}
```

**Alternatives considered**:
- Plain functions with piping (`|>`) — simpler but less readable for
  multi-field definitions; good for single operations but not for
  declaring a full server
- Attribute-based (mirror C# pattern) — not idiomatic F#; attributes
  are stringly-typed and lose type safety

---

## R3: Serialization Strategy

**Decision**: Use `System.Text.Json` with custom `JsonConverter<T>`
implementations for F# discriminated unions.

**Rationale**: The upstream SDK uses `System.Text.Json` internally. Using
the same serializer avoids double-serialization and keeps compatibility.
F# DUs need custom converters since `System.Text.Json` doesn't handle
them natively.

**Approach**:
- Write a `JsonConverter<Content>` that serializes/deserializes the
  `Content` DU (Text/Image/EmbeddedResource) matching the MCP JSON wire
  format
- Write converters for `ResourceContents`, `McpMessage`, etc.
- Roundtrip fidelity verified by FsCheck property tests
- Converters registered via `JsonSerializerOptions` shared across the
  toolkit

**Alternatives considered**:
- `FSharp.SystemTextJson` library — provides automatic DU serialization
  but may not match the exact MCP wire format; would add a dependency.
  Evaluate during implementation — if it handles the MCP JSON shape
  correctly, prefer it over hand-written converters.

---

## R4: Middleware Pipeline Design

**Decision**: Function-based middleware using
`McpContext -> (McpContext -> Task<McpResponse>) -> Task<McpResponse>`.

**Rationale**: This is the standard functional middleware pattern (same
shape as ASP.NET Core middleware but with F# types). It composes via
function application and doesn't require classes or interfaces.

**Design sketch**:
```fsharp
type McpMiddleware =
    McpContext -> (McpContext -> Task<McpResponse>) -> Task<McpResponse>

// Logging middleware
let loggingMiddleware (logger: ILogger) : McpMiddleware =
    fun ctx next -> task {
        logger.LogInformation("Request: {Method}", ctx.Method)
        let! response = next ctx
        logger.LogInformation("Response: {Status}", response.Status)
        return response
    }
```

**Alternatives considered**:
- Interface-based (`IMcpMiddleware`) — more familiar to C# devs but
  requires class definitions; not idiomatic F#
- Event-based (hooks) — less composable, harder to test

---

## R5: Async Strategy — Task Primary with Async Wrappers

**Decision**: Public API uses `Task<'T>`. Provide `Async` module with
convenience wrappers.

**Rationale**: The Microsoft MCP SDK is entirely `Task`-based. Wrapping
every call in `Async.AwaitTask` would add overhead and friction. F#
developers working with `task { }` CE (available since F# 6) find
`Task<'T>` natural. An `FsMcp.Async` module provides `Async<'T>` versions
for developers who prefer the classic F# async model.

**Approach**:
```fsharp
// Primary API (Task)
module FsMcp.Client
let callToolAsync (client: McpClient) (name: ToolName) (args: Map<string,obj>) : Task<Result<Content, McpError>> = ...

// Async convenience wrapper
module FsMcp.Client.Async
let callTool (client: McpClient) (name: ToolName) (args: Map<string,obj>) : Async<Result<Content, McpError>> =
    FsMcp.Client.callToolAsync client name args |> Async.AwaitTask
```

---

## R6: FsCheck Generator Strategy

**Decision**: Centralized `Generators` module per test project with
`Arbitrary` instances for all domain types.

**Rationale**: Having all generators in one place ensures consistency
and reuse. Generators compose — e.g., `ToolDefinition` generator uses
`ToolName` generator which uses `NonEmptyString` generator.

**Approach**:
- `Generators.fs` in each test project, ordered by dependency
- Register via `Arb.register<Generators>()` in test setup
- Generators produce only valid domain values (smart constructors
  ensure this)
- Separate generators for "invalid" inputs where needed for negative
  testing

---

## Summary of Resolved Items

| Item | Resolution |
|------|-----------|
| SDK to wrap | `ModelContextProtocol` NuGet (official) |
| DSL style | Computation expressions |
| Serialization | `System.Text.Json` + custom converters |
| Middleware shape | `ctx -> next -> Task<response>` |
| Async type | `Task<'T>` primary, `Async` wrappers |
| FsCheck generators | Centralized per test project |

No NEEDS CLARIFICATION items remain.
