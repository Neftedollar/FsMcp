---
sidebar_position: 1
slug: /
description: "FsMcp is an F# toolkit for building Model Context Protocol (MCP) servers and clients with type safety, computation expressions, and zero boilerplate."
---

# FsMcp

**Build MCP servers in F# with type safety, computation expressions, and zero boilerplate.**

FsMcp wraps Microsoft's [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) .NET SDK with an idiomatic F# API.

```fsharp
type GreetArgs = { name: string; greeting: string option }

let server = mcpServer {
    name "MyServer"
    version "1.0.0"

    tool (TypedTool.define<GreetArgs> "greet" "Greets" (fun args -> task {
        let g = args.greeting |> Option.defaultValue "Hello"
        return Ok [ Content.text $"{g}, {args.name}!" ]
    }) |> unwrapResult)

    useStdio
}

Server.run server |> fun t -> t.GetAwaiter().GetResult()
```

## Packages

| Package | Install | Description |
|---------|---------|-------------|
| **FsMcp.Server** | `dotnet add package FsMcp.Server` | Server builder CE, typed handlers, middleware, stdio |
| **FsMcp.Client** | `dotnet add package FsMcp.Client` | Typed client with Result-based errors |
| **FsMcp.Testing** | `dotnet add package FsMcp.Testing` | TestServer, assertions, FsCheck generators |
| **FsMcp.TaskApi** | `dotnet add package FsMcp.TaskApi` | FsToolkit.ErrorHandling pipeline |
| **FsMcp.Server.Http** | `dotnet add package FsMcp.Server.Http` | HTTP/SSE transport (opt-in ASP.NET) |
| **FsMcp.Sampling** | `dotnet add package FsMcp.Sampling` | LLM sampling from server tools |

## Why FsMcp?

- **`mcpServer { }` CE** — declare tools, resources, prompts in one block
- **`TypedTool.define<'T>`** — F# record as input, JSON Schema auto-generated via TypeShape
- **`Result<'T, McpError>`** — typed errors, no exceptions in expected paths
- **Smart constructors** — `ToolName.create` validates at construction, not at runtime
- **Composable middleware** — logging, validation, telemetry via pipeline
- **306 tests** — Expecto + FsCheck property tests on every domain type
