# FsMcp

**FsMcp is an idiomatic F# toolkit for building [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) servers and clients.** It wraps the official [Microsoft ModelContextProtocol .NET SDK](https://github.com/modelcontextprotocol/csharp-sdk) with computation expressions, typed tool handlers, Result-based error handling, and composable middleware — so you can build MCP servers in F# with type safety and zero boilerplate.

[![CI](https://github.com/Neftedollar/FsMcp/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/FsMcp/actions)
[![NuGet](https://img.shields.io/nuget/v/FsMcp.Server.svg)](https://www.nuget.org/packages/FsMcp.Server)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-neftedollar.github.io%2FFsMcp-blue)](https://neftedollar.github.io/FsMcp/)

```fsharp
type GreetArgs = { name: string; greeting: string option }

let server = mcpServer {
    name "MyServer"
    version "1.0.0"

    tool (TypedTool.define<GreetArgs> "greet" "Greets a person" (fun args -> task {
        let greeting = args.greeting |> Option.defaultValue "Hello"
        return Ok [ Content.text $"{greeting}, {args.name}!" ]
    }) |> unwrapResult)

    useStdio
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
dotnet add package FsMcp.Server.Http  # HTTP/SSE transport (opt-in ASP.NET)
dotnet add package FsMcp.Sampling   # LLM sampling from server tools
```

## Why FsMcp?

- **`mcpServer { }` CE** — declare tools, resources, prompts in a single block
- **`TypedTool.define<'T>`** — F# record as input, JSON Schema auto-generated via TypeShape
- **`Result<'T, McpError>`** — no exceptions in expected paths, typed errors everywhere
- **Smart constructors** — `ToolName.create` validates at construction, not at runtime
- **Composable middleware** — logging, validation, telemetry via `Middleware.pipeline`
- **322 tests** — Expecto + FsCheck property tests on every domain type

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

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args -> task {
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<CalcArgs> "divide" "Divide a by b" (fun args -> task {
        if args.b = 0.0 then return Error (TransportError "Division by zero")
        else return Ok [ Content.text $"{args.a / args.b}" ]
    }) |> unwrapResult)

    useStdio
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
│ Middleware     Serialization (JSON)          │                   │
│ Streaming      Interop (internal)           │                   │
│ Telemetry                                   │                   │
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
| **FsMcp.Server** | `mcpServer { }` CE, typed handlers, middleware, stdio transport |
| **FsMcp.Server.Http** | HTTP/SSE transport via ASP.NET Core (opt-in) |
| **FsMcp.Client** | Typed client with `Result<'T, McpError>` |
| **FsMcp.Testing** | `TestServer.callTool`, `Expect.mcp*`, FsCheck generators |
| **FsMcp.TaskApi** | `taskResult { }` pipeline via FsToolkit.ErrorHandling |
| **FsMcp.Sampling** | Server-side LLM invocation via MCP sampling |

## Features

- **Typed tool handlers** — `TypedTool.define<'T>` with TypeShape-powered JSON Schema + caching
- **Nested CE** — `mcpTool { toolName "..."; typedHandler ... }`
- **Streaming tools** — `StreamingTool.define` with `IAsyncEnumerable<Content>`
- **Notifications** — `ContextualTool.define` with progress + log callbacks
- **Validation middleware** — auto-validates args against schema before handler
- **Telemetry** — `Telemetry.tracing()` (Activity/OTel) + `MetricsCollector`
- **Hot reload** — `DynamicServer.addTool` / `removeTool` at runtime
- **Error handling** — `FsToolkit.ErrorHandling` integration via `FsMcp.TaskApi`

## Build & Test

```bash
dotnet build       # 7 packages
dotnet test        # 322 tests (Expecto + FsCheck)
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
5. **Composable** — middleware, function handlers, no inheritance

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Issues and PRs welcome.

## License

[MIT](LICENSE)
