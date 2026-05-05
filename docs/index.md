---
title: FsMcp — F# MCP Toolkit
category: Overview
categoryindex: 0
index: 0
---

# FsMcp

**Build MCP servers in F# with type safety, computation expressions, and zero boilerplate.**

FsMcp wraps Microsoft's official [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) .NET SDK with an idiomatic F# API.

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
```

## Packages

| Package | Description |
|---------|-------------|
| [FsMcp.Core](types-reference.html) | Domain types, validation, serialization |
| [FsMcp.Server](server-guide.html) | Server builder CE, typed handlers, middleware |
| [FsMcp.Server.Http](server-guide.html#http-transport) | HTTP/SSE transport (opt-in ASP.NET) |
| [FsMcp.Client](client-guide.html) | Typed client with Result-based errors |
| [FsMcp.Testing](testing-guide.html) | Test helpers, assertions, FsCheck generators |
| [FsMcp.TaskApi](client-guide.html#pipeline-api) | FsToolkit.ErrorHandling pipeline |
| [FsMcp.Sampling](advanced.html#sampling) | LLM sampling from server tools |

## Guides

- [Getting Started](getting-started.html) — install, hello world, run in 5 min
- [Server Guide](server-guide.html) — CE, typed handlers, resources, prompts
- [Middleware Guide](middleware-guide.html) — logging, auth, validation, telemetry
- [Client Guide](client-guide.html) — connect, call tools, error handling
- [Testing Guide](testing-guide.html) — TestServer, assertions, property testing
- [Advanced](advanced.html) — streaming, notifications, hot reload, sampling
- [Runtime Tuning](runtime-tuning.html) — GC configuration, memory diagnostics for stdio servers
- [Types Reference](types-reference.html) — all types and smart constructors
- [API Reference](reference/index.html) — auto-generated from XML docs
