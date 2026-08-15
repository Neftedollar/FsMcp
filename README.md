# FsMcp

**FsMcp is an idiomatic F# toolkit for building [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) servers and clients.** It wraps the official [Microsoft ModelContextProtocol .NET SDK](https://github.com/modelcontextprotocol/csharp-sdk) with computation expressions, typed cancellable handlers, Result-based error handling, secure ASP.NET Core composition, and enterprise-managed authorization.

[![CI](https://github.com/Neftedollar/FsMcp/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/FsMcp/actions)
[![NuGet](https://img.shields.io/nuget/v/FsMcp.Server.svg)](https://www.nuget.org/packages/FsMcp.Server)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-neftedollar.com%2FFsMcp-blue)](https://neftedollar.com/FsMcp/)

```fsharp
type GreetArgs = { name: string; greeting: string option }

let server = mcpServer {
    name "MyServer"
    version "1.0.0"

    tool (TypedTool.define<GreetArgs> "greet" "Greets a person" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        let greeting = args.greeting |> Option.defaultValue "Hello"
        return Ok [ Content.text $"{greeting}, {args.name}!" ]
    }) |> unwrapResult)
}

Server.run server |> fun t -> t.GetAwaiter().GetResult()
// Input schema auto-generated: name=required, greeting=optional
```

## Install

```bash
dotnet add package FsMcp.Server     # server builder + stdio transport
dotnet add package FsMcp.Client     # typed client wrapper
dotnet add package FsMcp.Testing    # test helpers + FsCheck generators
dotnet add package FsMcp.TaskApi    # FsToolkit.ErrorHandling pipeline
dotnet add package FsMcp.Server.Http  # Streamable HTTP transport (opt-in ASP.NET)
dotnet add package FsMcp.Sampling   # sampling types and explicit test helpers
```

## Why FsMcp?

- **`mcpServer { }` CE** — declare tools, resources, prompts in a single block
- **`TypedTool.define<'T>`** — F# record as input, JSON Schema auto-generated via TypeShape
- **`Result<'T, McpError>`** — no exceptions in expected paths, typed errors everywhere
- **Smart constructors** — `ToolName.create` validates at construction, not at runtime
- **Cancellable handlers** — the protocol request token reaches F# tool, resource, and prompt handlers
- **Secure hosting composition** — caller-owned DI, typed SDK filters, and ASP.NET authorization
- **Enterprise-managed authorization** — typed ID-JAG client flow with bounded refresh/retry
- **Broad test suite** — Expecto + FsCheck properties plus real wire/transport tests

## Quick Start

### Server with typed tools

```fsharp
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

type CalcArgs = { a: float; b: float }

let server = mcpServer {
    name "Calculator"
    version "1.0.0"

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<CalcArgs> "divide" "Divide a by b" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        if args.b = 0.0 then return Error (TransportError "Division by zero")
        else return Ok [ Content.text $"{args.a / args.b}" ]
    }) |> unwrapResult)
}

Server.run server |> fun t -> t.GetAwaiter().GetResult()
```

### HTTP transport

```bash
dotnet add package FsMcp.Server.Http
```

```fsharp
open FsMcp.Server.Http

HttpServer.run server (Some "/mcp") "http://localhost:3001"
|> fun t -> t.GetAwaiter().GetResult()
```

### Client

```fsharp
open FsMcp.Core.Validation
open FsMcp.Client

let demo () = task {
    let config = {
        Transport = ClientTransport.stdio "dotnet" ["run"; "--project"; "../Calculator"]
        Name = "TestClient"
        ShutdownTimeout = None
    }
    let! client = McpClient.connect config
    let! tools = McpClient.listTools client

    let toolName = ToolName.create "add" |> unwrapResult
    let args = Map.ofList [
        "a", System.Text.Json.JsonDocument.Parse("10").RootElement
        "b", System.Text.Json.JsonDocument.Parse("20").RootElement
    ]
    let! result = McpClient.callTool client toolName args
    // result : Result<Content list, McpError>
}
```

### Sampling status

`SamplingTool.define` is deliberately fail-closed in 2.0: its 1.x transport
path never reached the connected client. Use the SDK request-scoped sampling
primitive directly until FsMcp exposes a correctly wired replacement.

### Enterprise-managed authorization

`FsMcp.Client` 2.0 includes an F#-first wrapper for the stable MCP ID-JAG
enterprise authorization profile: validated opaque configuration, bounded
single-flight token caching, cancellation, redacted failures, same-origin
Bearer injection, and one controlled refresh/retry after `401`.

See the
[Enterprise-Managed Authorization guide](docs/enterprise-managed-authorization.md)
for the client flow and the required ASP.NET Core resource-server protection.

### Testing

```fsharp
open FsMcp.Testing

// Direct handler invocation — no network, no process spawning
let result =
    TestServer.callTool serverConfig "add"
        (Map.ofList ["a", jsonEl 10; "b", jsonEl 20])
    |> Async.AwaitTask |> Async.RunSynchronously

result |> Expect.mcpHasTextContent "30" "addition works"
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Your F# Code                             │
│   mcpServer { tool ...; resource ...; prompt ... }              │
├──────────────┬──────────────────────────────┬───────────────────┤
│ FsMcp.Server │       FsMcp.Core             │   FsMcp.Client    │
│              │                              │                   │
│ CE builder     Types (DUs, records)         │ Typed wrapper     │
│ TypedHandlers  Validation (smart ctors)     │ Async module      │
│ DI composition Serialization (JSON)          │ EMA / ID-JAG      │
│ SDK filters    Interop (internal)           │                   │
├──────────────┴──────────────────────────────┴───────────────────┤
│              Microsoft ModelContextProtocol SDK                  │
├─────────────────────────────────────────────────────────────────┤
│                      .NET 10 Runtime                            │
└─────────────────────────────────────────────────────────────────┘
```

## Packages

| Package | What it does |
|---------|-------------|
| **FsMcp.Core** | Domain types, smart constructors, JSON serialization |
| **FsMcp.Server** | `mcpServer { }` CE, cancellable typed handlers, stdio, DI composition |
| **FsMcp.Server.Http** | Streamable HTTP transport and ASP.NET Core composition |
| **FsMcp.Client** | Typed client plus enterprise-managed authorization |
| **FsMcp.Testing** | `TestServer.callTool`, `Expect.mcp*`, FsCheck generators |
| **FsMcp.TaskApi** | `taskResult { }` pipeline via FsToolkit.ErrorHandling |
| **FsMcp.Sampling** | Sampling domain types and explicit test helpers; legacy transport wiring fails closed |

## Features

- **Typed tool handlers** — `TypedTool.define<'T>` with TypeShape-powered JSON Schema + caching
- **Nested CE** — `mcpTool { toolName "..."; typedHandler ... }`
- **Streaming tools** — `StreamingTool.define` with `IAsyncEnumerable<Content>`
- **Explicit request cancellation** — every primary handler receives the SDK token
- **Secure hosting composition** — register FsMcp into caller-owned SDK/ASP.NET builders
- **Enterprise authorization** — stable ID-JAG client profile with bounded security defaults
- **Error handling** — `FsToolkit.ErrorHandling` integration via `FsMcp.TaskApi`

## Build & Test

```bash
dotnet build       # 7 packages
dotnet test        # Expecto + FsCheck + real transport tests
```

## Runtime tuning for stdio servers

By default .NET runs the Server GC, which is throughput-optimized and does not proactively return committed heap pages to the OS. For an idle stdio MCP server this can look like a memory leak — RSS grows during a session and stays elevated even when the server is quiet. The runtime releases the memory immediately once the OS signals memory pressure, confirming it was commit-grow, not a genuine leak.

Set these environment variables to reduce idle RSS:

```bash
DOTNET_gcServer=0      # Workstation GC — returns memory at idle
DOTNET_gcConcurrent=1  # Concurrent collection — shorter pauses
```

See [docs/runtime-tuning.md](docs/runtime-tuning.md) for the full explanation, MCP client config examples (Claude Code, Codex), a `runtimeconfig.template.json` snippet for redistributable tools, and a five-minute diagnostic recipe to distinguish commit-grow from an actual leak.

## Examples

See [`examples/`](examples/) for runnable MCP servers:
- **EchoServer** — echo + reverse tools, resource, prompt
- **Calculator** — add/subtract/multiply/divide
- **FileServer** — read_file, list_directory, file_info

## Design Principles

1. **Wrap, don't reimplement** — protocol concerns stay in Microsoft SDK
2. **Idiomatic F#** — DUs, Result, CEs, pipe-friendly
3. **Type safety** — private constructors, no `obj` in public API
4. **Test-first** — Expecto + FsCheck on every function
5. **Honest boundaries** — no authentication, transport, or cancellation behavior is implied unless it is wired

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Issues and PRs welcome.

## License

[MIT](LICENSE)
