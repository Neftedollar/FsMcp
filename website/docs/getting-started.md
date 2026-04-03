---
sidebar_position: 2
---


# Getting Started with FsMcp

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.100 or later)
- An editor with F# support (VS Code + Ionide, Rider, or Visual Studio)

Verify your SDK:

```bash
dotnet --version
# 10.0.100 or higher
```

## Install packages

Create a new console project and add the server package:

```bash
dotnet new console -lang F# -n MyMcpServer
cd MyMcpServer
dotnet add package FsMcp.Server
```

Other packages you may need later:

```bash
dotnet add package FsMcp.Client       # typed client wrapper
dotnet add package FsMcp.Testing      # test helpers + FsCheck generators
dotnet add package FsMcp.TaskApi      # FsToolkit.ErrorHandling pipeline
dotnet add package FsMcp.Server.Http  # HTTP/SSE transport (opt-in ASP.NET)
dotnet add package FsMcp.Sampling     # LLM sampling from server tools
```

## Hello world: minimal MCP server

Replace `Program.fs` with:

```fsharp
open FsMcp.Core
open FsMcp.Server

type GreetArgs = { name: string }

let server = mcpServer {
    name "HelloServer"
    version "1.0.0"

    tool (TypedTool.define<GreetArgs> "greet" "Say hello" (fun args -> task {
        return Ok [ Content.text $"Hello, {args.name}!" ]
    }) |> unwrapResult)

    useStdio
}

[<EntryPoint>]
let main _ =
    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
```

That is a complete, working MCP server in 15 lines. `TypedTool.define<GreetArgs>` auto-generates the JSON Schema from the `GreetArgs` record (the `name` field becomes a required `string` property). `unwrapResult` extracts the `Ok` value or throws with a descriptive error if the tool name is invalid.

Build and verify:

```bash
dotnet build
```

## Test with Claude Desktop

Add this to your Claude Desktop config (`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS, `%APPDATA%\Claude\claude_desktop_config.json` on Windows):

```json
{
  "mcpServers": {
    "hello": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/MyMcpServer"]
    }
  }
}
```

Restart Claude Desktop. You should see the "greet" tool available. Ask Claude to greet someone and it will call your server.

## Where to go next

- [Server Guide](server-guide.md) -- tools, resources, prompts, the `mcpServer { }` CE in depth
- [Middleware Guide](middleware-guide.md) -- logging, auth, validation, telemetry
- [Client Guide](client-guide.md) -- connect to MCP servers from F#
- [Testing Guide](testing-guide.md) -- test handlers without transport, property testing
- [Advanced](advanced.md) -- streaming, contextual tools, dynamic servers, sampling
- [Types Reference](types-reference.md) -- every domain type and smart constructor
