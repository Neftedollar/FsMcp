---
title: Server Guide
category: Guides
categoryindex: 1
index: 0
---

# Server Guide

## The `mcpServer { }` computation expression

Every FsMcp server starts with the `mcpServer` CE. It collects your configuration and produces a validated `ServerConfig`:

```fsharp
open FsMcp.Core
open FsMcp.Server

let server = mcpServer {
    name "MyServer"           // required -- ServerName (non-empty)
    version "1.0.0"           // required -- ServerVersion (non-empty)
    tool myToolDefinition     // zero or more tools
    resource myResource       // zero or more resources
    prompt myPrompt           // zero or more prompts
}
```

Missing `name` or `version` raises `FsMcpConfigException` with a message telling you exactly what to add. Duplicate tool names, resource URIs, or prompt names also raise `FsMcpConfigException`.

`ServerConfig` is transport-agnostic. Choose stdio with `Server.run`, or add
Streamable HTTP with `HttpServer`. The legacy `middleware` operation is obsolete:
FsMcp 1.x accepted those declarations but never executed them, so 2.0 rejects a
non-empty legacy middleware list during registration. Use official SDK request
filters or ASP.NET Core middleware instead.

## Untyped tools with `Tool.define`

For simple tools where you parse arguments manually from `Map<string, JsonElement>`:

```fsharp
open System.Text.Json
open FsMcp.Core
open FsMcp.Server

let echoTool =
    Tool.define "echo" "Echoes the message back" (fun args cancellationToken ->
        cancellationToken.ThrowIfCancellationRequested()
        let msg =
            args
            |> Map.tryFind "message"
            |> Option.map (fun j -> j.GetString())
            |> Option.defaultValue "(no message)"
        task { return Ok [ Content.text $"Echo: {msg}" ] })
    |> unwrapResult
```

`Tool.define` returns `Result<ToolDefinition, ValidationError>`. Use `unwrapResult` to extract the value or fail with a descriptive error.

The handler signature is:

```
Map<string, JsonElement>
    -> CancellationToken
    -> Task<Result<Content list, McpError>>
```

## Typed tools with `TypedTool.define<'T>`

Define an F# record for your input. TypeShape inspects it at startup and generates a JSON Schema automatically. Option fields become optional in the schema:

```fsharp
type ReverseArgs = { text: string; uppercase: bool option }

let reverseTool =
    TypedTool.define<ReverseArgs> "reverse" "Reverses the text" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        let reversed = args.text |> Seq.rev |> System.String.Concat
        let result =
            if args.uppercase |> Option.defaultValue false
            then reversed.ToUpper()
            else reversed
        return Ok [ Content.text result ]
    }) |> unwrapResult
```

The generated schema has `text` as required and `uppercase` as optional (not in the `required` array, nullable). The handler receives a deserialized `ReverseArgs` directly -- no manual JSON parsing.

## The `mcpTool { }` nested CE

For more control over tool construction, use the nested `mcpTool` CE:

```fsharp
let myTool = mcpTool {
    toolName "calculate"
    description "Performs a calculation"
    handler (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        let a = args |> Map.tryFind "a" |> Option.map (fun j -> j.GetDouble()) |> Option.defaultValue 0.0
        let b = args |> Map.tryFind "b" |> Option.map (fun j -> j.GetDouble()) |> Option.defaultValue 0.0
        return Ok [ Content.text $"{a + b}" ]
    })
}
```

For typed handlers with the `mcpTool` CE, use `TypedHandler.create<'T>`:

```fsharp
type CalcArgs = { a: float; b: float }

let typedCalcTool = mcpTool {
    toolName "add"
    description "Add two numbers"
    typedHandler (TypedHandler.create<CalcArgs> (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        return Ok [ Content.text $"{args.a + args.b}" ]
    }))
}
```

`TypedHandler.create<'T>` returns a `TypedHandlerInfo` with the raw handler and the auto-generated schema. The `typedHandler` operation wires both into the tool definition.

## Resources with `Resource.define`

Resources expose data that clients can read. The handler receives `Map<string, string>`:

```fsharp
open FsMcp.Core.Validation

let statusResource =
    Resource.define "info://server/status" "Server Status" (fun _ cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        let uri = ResourceUri.create "info://server/status" |> unwrapResult
        let mime = MimeType.create "application/json" |> unwrapResult
        return Ok (TextResource (uri, mime, """{"status":"running"}"""))
    }) |> unwrapResult
```

Resource URIs must be absolute with a scheme (e.g., `https://`, `file:///`, `info://`).

## Typed resources with `TypedResource.define<'T>`

```fsharp
type FileArgs = { path: string }

let fileResource =
    TypedResource.define<FileArgs> "file:///docs" "Documentation files" (fun args cancellationToken -> task {
        let uri = ResourceUri.create $"file:///{args.path}" |> unwrapResult
        let mime = MimeType.create "text/plain" |> unwrapResult
        let! content = System.IO.File.ReadAllTextAsync(args.path, cancellationToken)
        return Ok (TextResource (uri, mime, content))
    }) |> unwrapResult
```

## Prompts with `Prompt.define`

Prompts define reusable conversation templates:

```fsharp
let summarizePrompt =
    Prompt.define "summarize"
        [ { Name = "topic"; Description = Some "The topic to summarize"; Required = true } ]
        (fun args cancellationToken -> task {
            cancellationToken.ThrowIfCancellationRequested()
            let topic = args |> Map.tryFind "topic" |> Option.defaultValue "unknown"
            return Ok [
                { Role = User; Content = Content.text $"Please summarize {topic}." }
                { Role = Assistant; Content = Content.text $"Here is a summary of {topic}." }
            ]
        })
    |> unwrapResult
```

## Typed prompts with `TypedPrompt.define<'T>`

Arguments are inferred from the record. Option fields become non-required prompt arguments:

```fsharp
type SummarizeArgs = { topic: string; style: string option }

let typedSummarize =
    TypedPrompt.define<SummarizeArgs> "summarize" "Summarize a topic" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        let style = args.style |> Option.defaultValue "concise"
        return Ok [
            { Role = User; Content = Content.text $"Summarize {args.topic} in a {style} style." }
        ]
    }) |> unwrapResult
```

## Running the server

### Stdio transport

```fsharp
[<EntryPoint>]
let main _ =
    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
```

Or with `Async`:

```fsharp
[<EntryPoint>]
let main _ =
    Server.runAsync server |> Async.RunSynchronously
    0
```

### HTTP transport

Install the HTTP package:

```bash
dotnet add package FsMcp.Server.Http
```

```fsharp
open FsMcp.Server.Http

[<EntryPoint>]
let main _ =
    HttpServer.run server (Some "/mcp") "http://localhost:5000"
    |> fun t -> t.GetAwaiter().GetResult()
    0
```

`HttpServer.run` takes the `ServerConfig`, an optional route endpoint (defaults to `"/"`), and the URL to listen on. It uses ASP.NET Core with Streamable HTTP.

For a caller-owned ASP.NET Core host, prefer
`HttpServer.addToServices config services` (or `addToBuilder`). Stateful HTTP
composition owns a bounded, opaque resource-subscription registry and removes a
session's entries when it disconnects. If `HttpServerTransportOptions.Stateless`
is enabled, FsMcp omits the `resources.subscribe` capability while retaining
fail-closed handlers: direct subscribe/unsubscribe requests are rejected and
never retain subscription state. `runWithSubscriptions` exposes the opaque
registry handle when the host needs to publish `notifications/resources/updated`.

## Full example combining everything

```fsharp
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

type CalcArgs = { a: float; b: float }
type EchoArgs = { message: string }

let server = mcpServer {
    name "DemoServer"
    version "1.0.0"

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<EchoArgs> "echo" "Echo a message" (fun args cancellationToken -> task {
        cancellationToken.ThrowIfCancellationRequested()
        return Ok [ Content.text $"Echo: {args.message}" ]
    }) |> unwrapResult)

    resource (
        Resource.define "info://demo/version" "Version Info" (fun _ cancellationToken -> task {
            cancellationToken.ThrowIfCancellationRequested()
            let uri = ResourceUri.create "info://demo/version" |> unwrapResult
            let mime = MimeType.create "text/plain" |> unwrapResult
            return Ok (TextResource (uri, mime, "1.0.0"))
        }) |> unwrapResult)

    prompt (
        Prompt.define "explain"
            [ { Name = "topic"; Description = Some "Topic to explain"; Required = true } ]
            (fun args cancellationToken -> task {
                cancellationToken.ThrowIfCancellationRequested()
                let topic = args |> Map.tryFind "topic" |> Option.defaultValue "something"
                return Ok [
                    { Role = User; Content = Content.text $"Explain {topic} simply." }
                ]
            })
        |> unwrapResult)
}

[<EntryPoint>]
let main _ =
    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
```
