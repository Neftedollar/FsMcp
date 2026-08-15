---
title: FsMcp — F# MCP Toolkit
category: Overview
categoryindex: 0
index: 0
---

# FsMcp

**Build cancellable MCP servers and secure clients in idiomatic F#.**

FsMcp wraps Microsoft's official [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) .NET SDK with an idiomatic F# API.

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
```

## Packages

| Package | Description |
|---------|-------------|
| [FsMcp.Core](types-reference.html) | Domain types, validation, serialization |
| [FsMcp.Server](server-guide.html) | Server builder CE, cancellable handlers, stdio and DI composition |
| [FsMcp.Server.Http](server-guide.html#http-transport) | Streamable HTTP and ASP.NET Core composition |
| [FsMcp.Client](client-guide.html) | Typed client and enterprise-managed authorization |
| [FsMcp.Testing](testing-guide.html) | Test helpers, assertions, FsCheck generators |
| [FsMcp.TaskApi](client-guide.html#pipeline-api) | FsToolkit.ErrorHandling pipeline |
| [FsMcp.Sampling](advanced.html#sampling-package-boundary) | Sampling types and explicit test helpers; legacy wiring fails closed |

## Guides

- [Getting Started](getting-started.html) — install, hello world, run in 5 min
- [Server Guide](server-guide.html) — CE, typed handlers, resources, prompts
- [Middleware Migration](middleware-guide.html) — SDK filters and ASP.NET Core middleware
- [Enterprise Authorization](enterprise-managed-authorization.html) — ID-JAG client flow and server protection
- [Client Guide](client-guide.html) — connect, call tools, error handling
- [Testing Guide](testing-guide.html) — TestServer, assertions, property testing
- [Advanced](advanced.html) — streaming, subscriptions, SDK extension points, sampling boundaries
- [Runtime Tuning](runtime-tuning.html) — GC configuration, memory diagnostics for stdio servers
- [Types Reference](types-reference.html) — all types and smart constructors
- [API Reference](reference/index.html) — auto-generated from XML docs
