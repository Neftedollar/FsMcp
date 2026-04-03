# FsMcp — Idiomatic F# Toolkit for the Model Context Protocol

FsMcp wraps Microsoft's official [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) .NET SDK with a functional, type-safe F# API.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Your F# Code                             │
│                                                                 │
│   mcpServer {                    McpClient.connect config       │
│     name "MyServer"              McpClient.callTool client ...  │
│     tool (Tool.define ...)       McpClient.listResources ...    │
│     resource (Resource.define)                                  │
│     prompt (Prompt.define)                                      │
│     useStdio / useHttp                                          │
│   }                                                             │
├─────────────┬───────────────────────────────┬───────────────────┤
│ FsMcp.Server│        FsMcp.Core             │   FsMcp.Client    │
│             │                               │                   │
│ ServerBuilder (CE)  Types & DUs             │ Typed client      │
│ Handlers          Validation (smart ctors)  │ Transport helpers │
│ Middleware        Serialization (JSON)       │ Async wrappers   │
│ Transport         Interop (internal)        │                   │
├─────────────┴───────────────────────────────┴───────────────────┤
│              Microsoft ModelContextProtocol SDK                  │
│         (transport, JSON-RPC, capability negotiation)           │
├─────────────────────────────────────────────────────────────────┤
│                   .NET 10 Runtime                               │
└─────────────────────────────────────────────────────────────────┘
```

## Projects

```
FsMcp.sln
├── src/
│   ├── FsMcp.Core/          # Domain types, validation, serialization
│   ├── FsMcp.Server/        # Server builder DSL, handlers, middleware, transport
│   ├── FsMcp.Client/        # Client wrapper with typed results
│   └── FsMcp.Testing/       # Test helpers: assertions, generators, test server
└── tests/
    ├── FsMcp.Core.Tests/    # 119 tests (Expecto + FsCheck property tests)
    ├── FsMcp.Server.Tests/  # 39 tests (builder, handlers, middleware, interop)
    ├── FsMcp.Client.Tests/  # 14 tests + 8 integration (pending)
    └── FsMcp.Testing.Tests/ # 55 tests (expect helpers, generators, test server)
```

## Quick Start

### Define and run an MCP server

```fsharp
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

let server = mcpServer {
    name "MyServer"
    version "1.0.0"

    tool (
        Tool.define "greet" "Greets a person by name" (fun args -> task {
            let name =
                args
                |> Map.tryFind "name"
                |> Option.map (fun j -> j.GetString())
                |> Option.defaultValue "World"
            return Ok [ Content.text $"Hello, {name}!" ]
        })
        |> unwrapResult)

    resource (
        Resource.define "config://settings" "App Settings" (fun _ -> task {
            let uri = ResourceUri.create "config://settings" |> unwrapResult
            let mime = MimeType.create "application/json" |> unwrapResult
            return Ok (TextResource (uri, mime, """{"theme":"dark"}"""))
        })
        |> unwrapResult)

    useStdio
}

Server.run server |> fun t -> t.GetAwaiter().GetResult()
```

### Run over HTTP instead

Add `FsMcp.Server.Http` package (separate — no ASP.NET dependency for stdio-only servers):

```fsharp
open FsMcp.Server.Http

HttpServer.run server (Some "/mcp") "http://localhost:3001"
|> fun t -> t.GetAwaiter().GetResult()
```

### Connect as a client

```fsharp
open FsMcp.Client

let demo () = task {
    let config = {
        Transport = ClientTransport.stdio "dotnet" ["run"; "--project"; "../MyServer"]
        Name = "TestClient"
        ShutdownTimeout = None
    }
    let! client = McpClient.connect config
    let! tools = McpClient.listTools client

    for t in tools do
        printfn "Tool: %s — %s" t.Name t.Description

    let toolName = ToolName.create "greet" |> unwrapResult
    let args = Map.ofList [
        "name", System.Text.Json.JsonDocument.Parse("\"FsMcp\"").RootElement
    ]
    let! result = McpClient.callTool client toolName args
    match result with
    | Ok contents -> for c in contents do printfn "%A" c
    | Error err -> printfn "Error: %A" err

    do! McpClient.disconnect client
}
```

### Test your server

```fsharp
open FsMcp.Testing

// Direct handler testing (no transport needed)
let result = TestServer.callTool serverConfig "greet" (Map.ofList ["name", jsonElement])
              |> Async.AwaitTask |> Async.RunSynchronously

// Assertion helpers
result |> Expect.mcpHasTextContent "Hello, FsMcp!" "greet response"

// FsCheck generators for property testing
McpArbitraries.register ()
testPropertyWithConfig config "tool handles any valid input"
    <| fun (name: ToolName) -> ...
```

## Type System

```
Identifiers (single-case DUs, private constructors):
  ToolName ──── ToolName.create : string -> Result<ToolName, ValidationError>
  ResourceUri ─ ResourceUri.create : string -> Result<ResourceUri, ValidationError>
  PromptName ── PromptName.create : string -> Result<PromptName, ValidationError>
  MimeType ──── MimeType.create : string -> Result<MimeType, ValidationError>
  ServerName    ServerVersion

Content (DU):
  Text of string
  Image of byte[] * MimeType
  EmbeddedResource of ResourceContents

ResourceContents (DU):
  TextResource of ResourceUri * MimeType * string
  BlobResource of ResourceUri * MimeType * byte[]

McpError (DU):
  ValidationFailed | ToolNotFound | ResourceNotFound | PromptNotFound
  HandlerException | TransportError | ProtocolError

Server types:
  ServerConfig ── built by mcpServer { } CE
  McpMiddleware = McpContext -> (McpContext -> Task<McpResponse>) -> Task<McpResponse>
  Transport = Stdio | Http of string option
```

## Middleware

```fsharp
let loggingMiddleware (logger: ILogger) : McpMiddleware =
    fun ctx next -> task {
        logger.LogInformation("Request: {Method}", ctx.Method)
        let! response = next ctx
        return response
    }

let server = mcpServer {
    name "WithMiddleware"
    version "1.0.0"
    middleware (loggingMiddleware logger)
    tool (...)
    useStdio
}
```

Compose middleware: `Middleware.compose mw1 mw2` or `Middleware.pipeline [mw1; mw2; mw3]`

## Data Flow

```
                    ┌──────────┐
  Client Request    │ mcpServer│    Server.run / Server.runHttp
  ─────────────────►│   { }    │◄──────────────────────────────
                    │ CE builds│
                    │ Server   │
                    │ Config   │
                    └────┬─────┘
                         │
              ┌──────────▼──────────┐
              │ ServerConfig.validate│ (rejects duplicates)
              └──────────┬──────────┘
                         │
         ┌───────────────▼───────────────┐
         │      SDK Registration         │
         │                               │
         │ McpServerTool.Create(handler) │
         │ McpServerResource.Create(...)  │
         │ McpServerPrompt.Create(...)    │
         └───────────────┬───────────────┘
                         │
              ┌──────────▼──────────┐
              │   Microsoft SDK     │
              │ Host + Transport    │
              │ (stdio / HTTP+SSE)  │
              └─────────────────────┘
```

## Build & Test

```bash
dotnet build       # builds all 4 projects
dotnet test        # runs 227 tests (Expecto + FsCheck)
```

## Design Principles

1. **Wrap, don't reimplement** — All protocol concerns delegated to Microsoft SDK
2. **Idiomatic F#** — DUs, Result types, computation expressions, pipe-friendly
3. **Type safety** — Private constructors, smart validators, no `obj` in public API
4. **Test-first** — Expecto + FsCheck property tests on every function
5. **Extensibility** — Composable middleware, function-based handlers

## License

MIT
