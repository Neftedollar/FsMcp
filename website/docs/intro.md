---
sidebar_position: 1
slug: /
description: "FsMcp 2.0 is an idiomatic F# toolkit for cancellable MCP servers, typed clients, secure HTTP composition, and enterprise-managed authorization."
---

# FsMcp

**Build MCP servers and clients in F# with typed cancellation, explicit errors,
secure hosting composition, and honest runtime boundaries.**

FsMcp wraps Microsoft's [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) .NET SDK with an idiomatic F# API.

```fsharp
type GreetArgs = { name: string; greeting: string option }

let server = mcpServer {
    name "MyServer"
    version "1.0.0"

    tool (TypedTool.define<GreetArgs> "greet" "Greets" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        let g = args.greeting |> Option.defaultValue "Hello"
        return Ok [ Content.text $"{g}, {args.name}!" ]
    }) |> unwrapResult)
}

Server.run server |> fun t -> t.GetAwaiter().GetResult()
```

## Packages

| Package | Install | Description |
|---------|---------|-------------|
| **FsMcp.Core** | `dotnet add package FsMcp.Core` | Domain types, validation, serialization |
| **FsMcp.Server** | `dotnet add package FsMcp.Server` | Server builder CE, cancellable typed handlers, stdio |
| **FsMcp.Client** | `dotnet add package FsMcp.Client` | Typed client and enterprise-managed authorization |
| **FsMcp.Testing** | `dotnet add package FsMcp.Testing` | TestServer, assertions, FsCheck generators |
| **FsMcp.TaskApi** | `dotnet add package FsMcp.TaskApi` | FsToolkit.ErrorHandling pipeline |
| **FsMcp.Server.Http** | `dotnet add package FsMcp.Server.Http` | Streamable HTTP and ASP.NET Core composition |
| **FsMcp.Sampling** | `dotnet add package FsMcp.Sampling` | Sampling types and explicit test helpers |

## Why FsMcp?

- **`mcpServer { }` CE** — declare tools, resources, prompts in one block
- **`TypedTool.define<'T>`** — F# record as input, JSON Schema auto-generated via TypeShape
- **`Result<'T, McpError>`** — typed errors, no exceptions in expected paths
- **Smart constructors** — `ToolName.create` validates at construction, not at runtime
- **Request cancellation** — the SDK request token reaches every primary handler
- **Secure composition** — caller-owned DI, SDK filters, and ASP.NET authorization
- **Enterprise-managed authorization** — validated ID-JAG client flow with bounded refresh/retry
- **Fail-closed compatibility** — disconnected 1.x runtime surfaces no longer pretend to work

FsMcp 2.0 is a breaking release. `McpClient.readResource` returns the complete
`ResourceContents list`, handler signatures take a final `CancellationToken`,
and transport selection belongs to `Server.run` or `FsMcp.Server.Http`, not the
server computation expression.
