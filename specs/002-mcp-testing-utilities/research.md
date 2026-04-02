# Research: MCP Testing Utilities

**Feature Branch**: `002-mcp-testing-utilities`
**Created**: 2026-04-02

## Key Decisions

### KD-001: In-Memory Transport Strategy

**Question**: How to provide in-memory transport for testing when the
Microsoft MCP C# SDK does not ship one?

**Research**: The C# SDK (`ModelContextProtocol` NuGet) provides two
transport types:
- `StdioClientTransport` / `WithStdioServerTransport()` — process-based
- `HttpClientTransport` / `WithHttpTransport()` — network-based (SSE
  and Streamable HTTP)

Neither supports in-process testing. However, the SDK uses standard
`IClientTransport` / server transport abstractions internally, and the
stdio transport ultimately works over `Stream` instances (stdin/stdout).

**Decision**: Build an `InMemoryTransport` using `System.IO.Pipelines`
(`Pipe` class) to create two pairs of connected streams. The server
reads from one pipe and writes to another; the client does the reverse.
This plugs into the SDK's existing stream-based transport infrastructure
without reimplementing protocol concerns (JSON-RPC, message framing).

**Alternative considered**: Spawning a real server process and
connecting via stdio. Rejected because it is slow (~500ms+ startup per
test), requires file system access to the server binary, cannot run in
parallel reliably, and is inappropriate for unit tests.

**Alternative considered**: Using `System.IO.Pipelines.IDuplexPipe`
directly. This may work but `Pipe` pairs are simpler and the SDK
expects `Stream` instances for its transport. Paired `MemoryStream` with
`PipeReader/PipeWriter` adapters is the most compatible approach.

**Constitution alignment**: Principle I (Microsoft MCP Foundation) —
we use the SDK's stream-based transport mechanism, not reimplementing
JSON-RPC. Principle VII (Simplicity) — paired pipes are the simplest
correct approach.

---

### KD-002: Test Client Helper API Shape

**Question**: What is the ideal API for creating a test client?

**Decision**: Provide a `TestServer` module with these primary functions:

```fsharp
// Full control: returns both server and client for advanced scenarios
TestServer.start : McpServerDefinition -> Async<TestSession>

// One-call: returns just the connected client for simple tests
TestServer.connectClient : McpServerDefinition -> Async<IMcpClient>
```

Where `TestSession` is a disposable record containing both the server,
client, and transport — allowing tests to inspect server state or
dispose resources explicitly.

**Rationale**: Two levels of abstraction serve different needs.
`connectClient` covers 80% of use cases (simple tool invocation tests).
`start` covers advanced scenarios (testing middleware, inspecting server
state, testing lifecycle events).

**Constitution alignment**: Principle II (Idiomatic F#) — module +
let functions, pipe-friendly. Principle VII (Simplicity) — simple
case is simple, complex case is possible.

---

### KD-003: Assertion Helper Integration with Expecto

**Question**: Should assertion helpers be standalone functions or
Expecto custom matchers?

**Decision**: Standalone functions in an `Expect` sub-module that follow
Expecto's `Expect.___` naming convention. They take a message parameter
(like Expecto does) and throw `AssertException` with detailed failure
information on mismatch.

```fsharp
// Pattern: Expect.mcpXxx expectedValue message actualValue
result |> Expect.mcpHasTextContent "hello" "tool should echo input"
result |> Expect.mcpIsError "should fail on invalid input"
tools  |> Expect.mcpContainsTool "echo" "server should expose echo"
```

The pipe-last style aligns with Expecto convention and enables
chaining.

**Alternative considered**: Custom computation expression for
assertions. Rejected per Principle VII (Simplicity) — standard
functions are easier to discover and require no new concepts.

---

### KD-004: FsCheck Generator Scope

**Question**: Which MCP types need FsCheck generators?

**Decision**: Provide generators for the testing-relevant protocol
types:

1. **Tool call arguments** — random JSON objects with varying depth,
   types (string, number, boolean, null, array, nested object).
2. **Resource URIs** — valid URIs following MCP resource URI patterns.
3. **Prompt arguments** — key-value pairs of prompt template variables.
4. **Content payloads** — `TextContent`, `ImageContent`, and
   `EmbeddedResourceContent` discriminated union cases.
5. **Tool names** — valid non-empty strings following MCP naming rules.

Generators MUST produce values that pass through feature 001's smart
constructors (validation). Shrinking MUST work correctly for all
generators to produce minimal counterexamples.

**Constitution alignment**: Principle IV (Property-Based Testing) —
custom Arbitrary generators for domain types with shrinking verified.

---

### KD-005: Snapshot Testing Approach

**Question**: How should snapshot comparison work?

**Decision**: Use JSON serialization via `System.Text.Json` to capture
server responses. Store snapshots as `.json` files alongside tests.
Comparison uses semantic JSON equality (not string equality) to avoid
false positives from whitespace/key ordering differences.

Field exclusion uses a JSON path-like syntax:
```fsharp
Snapshot.verify
    (exclude = [ "$.timestamp"; "$.requestId" ])
    snapshotPath
    actualResponse
```

Update mode is controlled by an environment variable
(`FSMCP_UPDATE_SNAPSHOTS=1`) rather than a function parameter, so tests
don't need conditional logic.

**Constitution alignment**: Principle VII (Simplicity) — JSON files
are human-readable, environment variable for updates avoids API
complexity.

---

### KD-006: Dependency Injection for Mock Testing

**Question**: How to enable dependency injection for tool handlers in
the test context?

**Decision**: Leverage the Microsoft DI container that the MCP SDK
already uses. The `TestServer.start` function accepts an optional
`configureServices` parameter that lets tests register mock
implementations:

```fsharp
TestServer.start
    serverDefinition
    (configureServices = fun services ->
        services.AddSingleton<IFileReader>(mockFileReader) |> ignore)
```

This aligns with how the C# SDK's `AddMcpServer()` works with the DI
container, and doesn't require FsMcp to invent its own DI mechanism.

**Constitution alignment**: Principle I (Microsoft MCP Foundation) —
uses the SDK's existing DI infrastructure. Principle V (Extensibility)
— users can register any service.

## Technology Notes

- **System.IO.Pipelines**: Provides high-performance, low-allocation
  stream pairs. Available in .NET 8 via `System.IO.Pipelines` NuGet.
- **System.Text.Json**: Used for snapshot serialization, aligned with
  the upstream SDK's serializer choice.
- **Expecto.FsCheck**: Integration package for running FsCheck
  properties through Expecto's test runner.
