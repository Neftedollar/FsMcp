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
    middleware myMiddleware    // zero or more middleware
    useStdio                  // transport (default is stdio)
}
```

Missing `name` or `version` raises `FsMcpConfigException` with a message telling you exactly what to add. Duplicate tool names, resource URIs, or prompt names also raise `FsMcpConfigException`.

## Untyped tools with `Tool.define`

For simple tools where you parse arguments manually from `Map<string, JsonElement>`:

```fsharp
open System.Text.Json
open FsMcp.Core
open FsMcp.Server

let echoTool =
    Tool.define "echo" "Echoes the message back" (fun args ->
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
Map<string, JsonElement> -> Task<Result<Content list, McpError>>
```

## Typed tools with `TypedTool.define<'T>`

Define an F# record for your input. TypeShape inspects it at startup and generates a JSON Schema automatically. Option fields become optional in the schema:

```fsharp
type ReverseArgs = { text: string; uppercase: bool option }

let reverseTool =
    TypedTool.define<ReverseArgs> "reverse" "Reverses the text" (fun args -> task {
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
    handler (fun args -> task {
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
    typedHandler (TypedHandler.create<CalcArgs> (fun args -> task {
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
    Resource.define "info://server/status" "Server Status" (fun _ -> task {
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
    TypedResource.define<FileArgs> "file:///docs" "Documentation files" (fun args -> task {
        let uri = ResourceUri.create $"file:///{args.path}" |> unwrapResult
        let mime = MimeType.create "text/plain" |> unwrapResult
        let! content = System.IO.File.ReadAllTextAsync(args.path)
        return Ok (TextResource (uri, mime, content))
    }) |> unwrapResult
```

## Prompts with `Prompt.define`

Prompts define reusable conversation templates:

```fsharp
let summarizePrompt =
    Prompt.define "summarize"
        [ { Name = "topic"; Description = Some "The topic to summarize"; Required = true } ]
        (fun args -> task {
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
    TypedPrompt.define<SummarizeArgs> "summarize" "Summarize a topic" (fun args -> task {
        let style = args.style |> Option.defaultValue "concise"
        return Ok [
            { Role = User; Content = Content.text $"Summarize {args.topic} in a {style} style." }
        ]
    }) |> unwrapResult
```

## Running the server

### Stdio transport (default)

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

`HttpServer.run` takes the `ServerConfig`, an optional route endpoint (defaults to `"/"`), and the URL to listen on. It uses ASP.NET Core with Streamable HTTP + SSE.

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

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args -> task {
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<EchoArgs> "echo" "Echo a message" (fun args -> task {
        return Ok [ Content.text $"Echo: {args.message}" ]
    }) |> unwrapResult)

    resource (
        Resource.define "info://demo/version" "Version Info" (fun _ -> task {
            let uri = ResourceUri.create "info://demo/version" |> unwrapResult
            let mime = MimeType.create "text/plain" |> unwrapResult
            return Ok (TextResource (uri, mime, "1.0.0"))
        }) |> unwrapResult)

    prompt (
        Prompt.define "explain"
            [ { Name = "topic"; Description = Some "Topic to explain"; Required = true } ]
            (fun args -> task {
                let topic = args |> Map.tryFind "topic" |> Option.defaultValue "something"
                return Ok [
                    { Role = User; Content = Content.text $"Explain {topic} simply." }
                ]
            })
        |> unwrapResult)

    useStdio
}

[<EntryPoint>]
let main _ =
    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
```
