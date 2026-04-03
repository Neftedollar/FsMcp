---
sidebar_position: 7
description: "Advanced FsMcp features: streaming tools, contextual tools with progress notifications, dynamic servers, and LLM sampling from server tools."
---


# Advanced

## `StreamingTool.define` -- `IAsyncEnumerable<Content>` handlers

Streaming tools yield content items one at a time via `IAsyncEnumerable<Content>`. Items are collected and returned as a `Content list` when the enumeration completes:

```fsharp
open System.Collections.Generic
open System.Runtime.CompilerServices
open FsMcp.Core
open FsMcp.Server

let streamTool =
    StreamingTool.define "count" "Count to N" (fun args ->
        let n =
            args
            |> Map.tryFind "n"
            |> Option.map (fun j -> j.GetInt32())
            |> Option.defaultValue 5
        { new IAsyncEnumerable<Content> with
            member _.GetAsyncEnumerator(_) =
                let mutable i = 0
                { new IAsyncEnumerator<Content> with
                    member _.Current = Content.text $"Count: {i}"
                    member _.MoveNextAsync() =
                        i <- i + 1
                        System.Threading.Tasks.ValueTask<bool>(i <= n)
                    member _.DisposeAsync() =
                        System.Threading.Tasks.ValueTask() } })
    |> unwrapResult
```

### Typed streaming tools

`StreamingTool.defineTyped<'T>` combines typed input with streaming output:

```fsharp
type CountArgs = { n: int }

let typedStreamTool =
    StreamingTool.defineTyped<CountArgs> "count" "Count to N" (fun args ->
        { new IAsyncEnumerable<Content> with
            member _.GetAsyncEnumerator(_) =
                let mutable i = 0
                { new IAsyncEnumerator<Content> with
                    member _.Current = Content.text $"Count: {i}"
                    member _.MoveNextAsync() =
                        i <- i + 1
                        System.Threading.Tasks.ValueTask<bool>(i <= args.n)
                    member _.DisposeAsync() =
                        System.Threading.Tasks.ValueTask() } })
    |> unwrapResult
```

## `ContextualTool.define` -- handlers with progress/log notifications

Contextual tools receive a `HandlerContext` that lets them send progress and log notifications to the client during execution:

```fsharp
open FsMcp.Core
open FsMcp.Server
open FsMcp.Server.Notifications

type ProcessArgs = { data: string }

let contextualToolResult =
    ContextualTool.define<ProcessArgs> "process" "Process data with progress" (fun ctx args -> task {
        do! ctx.Log { Level = Info; Message = "Starting processing"; Logger = Some "process" }

        do! ctx.ReportProgress { Progress = 0.0; Message = Some "Initializing" }
        // ... do work ...
        do! ctx.ReportProgress { Progress = 0.5; Message = Some "Halfway done" }
        // ... do more work ...
        do! ctx.ReportProgress { Progress = 1.0; Message = Some "Complete" }

        do! ctx.Log { Level = Info; Message = "Processing complete"; Logger = Some "process" }

        return Ok [ Content.text $"Processed: {args.data}" ]
    })
```

`ContextualTool.define` returns `Result<ContextualToolHandle, ValidationError>`. The `ContextualToolHandle` contains:

- **`Definition`** -- the `ToolDefinition` to register with the server (uses a no-op context by default)
- **`InvokeWithContext`** -- invoke the handler with a specific `HandlerContext` (for testing or runtime wiring)

Register the tool in the server:

```fsharp
let handle = contextualToolResult |> unwrapResult

let server = mcpServer {
    name "ContextServer"
    version "1.0.0"
    tool handle.Definition
    useStdio
}
```

Test with a custom context:

```fsharp
let testCtx = HandlerContext.noOp  // progress/log are no-ops

let result =
    ContextualTool.invokeWithContext testCtx handle (Map.ofList [
        "data", System.Text.Json.JsonDocument.Parse("\"test\"").RootElement
    ])
    |> Async.AwaitTask |> Async.RunSynchronously
```

### Notification types

```fsharp
type ProgressUpdate = {
    Progress: float       // 0.0 to 1.0
    Message: string option
}

type McpLogLevel = Debug | Info | Warning | Error

type LogEntry = {
    Level: McpLogLevel
    Message: string
    Logger: string option
}
```

## `DynamicServer` -- add/remove tools at runtime

`DynamicServer` wraps a `ServerConfig` and supports hot-reloading tools:

```fsharp
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

let initialConfig = mcpServer {
    name "DynamicDemo"
    version "1.0.0"
    useStdio
}

let dynServer = DynamicServer.create initialConfig

// Add a tool at runtime
let newTool =
    Tool.define "hello" "Says hello" (fun _ -> task {
        return Ok [ Content.text "Hello!" ]
    }) |> unwrapResult

DynamicServer.addTool newTool dynServer

// Check tool count
let count = DynamicServer.toolCount dynServer  // 1

// Subscribe to changes
DynamicServer.onToolsChanged dynServer
|> Event.add (fun () -> printfn "Tools changed!")

// Remove a tool
let toolName = ToolName.create "hello" |> unwrapResult
DynamicServer.removeTool toolName dynServer
```

`addTool` raises `FsMcpConfigException` if a tool with the same name already exists. Call `removeTool` first to replace a tool.

## `SamplingTool.define` -- invoke client LLM from server

The `FsMcp.Sampling` package lets server-side tools ask the client's LLM to generate text:

```bash
dotnet add package FsMcp.Sampling
```

```fsharp
open FsMcp.Core
open FsMcp.Sampling

type SummarizeArgs = { text: string }

let summarizeTool =
    SamplingTool.define<SummarizeArgs> "ai_summarize" "Summarize text using the client LLM"
        (fun ctx args -> task {
            let request =
                SamplingRequest.simple $"Summarize this text:\n\n{args.text}" 200
                |> SamplingRequest.withTemperature 0.3

            let! result = ctx.Sample request
            match result with
            | Ok samplingResult ->
                match samplingResult.Message.Content with
                | Text t -> return Ok [ Content.text t ]
                | _ -> return Ok [ Content.text "[non-text response]" ]
            | Error SamplingNotSupported ->
                return Error (TransportError "Client does not support sampling")
            | Error (SamplingFailed msg) ->
                return Error (TransportError $"Sampling failed: {msg}")
            | Error SamplingTimeout ->
                return Error (TransportError "Sampling timed out")
            | Error (SamplingRejected reason) ->
                return Error (TransportError $"Sampling rejected: {reason}")
        })
    |> unwrapResult
```

### `SamplingRequest` builders

```fsharp
// Simple request with one user message
let req = SamplingRequest.simple "What is 2+2?" 100

// With a system prompt
let req2 = SamplingRequest.withSystem "You are a math tutor" "What is 2+2?" 100

// Pipeline style
let req3 =
    SamplingRequest.simple "Explain MCP" 500
    |> SamplingRequest.withTemperature 0.7
    |> SamplingRequest.withModel "claude-sonnet"
    |> SamplingRequest.withStopSequences [ "---" ]
```

### Testing sampling tools

Use `SamplingTool.mockContext` to provide a fixed response without a real LLM:

```fsharp
let mockCtx = SamplingTool.mockContext "This is a summary."
// or
let noOpCtx = SamplingTool.noOpContext ()  // always returns SamplingNotSupported
```

## TypeShape caching internals

TypeShape reflection is used to generate JSON Schemas from F# records. Both schema generation and option-field detection are cached per-type in `ConcurrentDictionary` instances inside the `SchemaGen` module:

- **`optionFieldsCache`** -- maps `Type` to the `Set<string>` of field names that are `option` types
- **`schemaCache`** -- maps `Type` to the generated `JsonElement` schema

This means the first call to `TypedTool.define<'T>` pays the reflection cost; subsequent calls for the same `'T` return instantly. Schemas are generated once at startup and never recomputed.

The schema generation marks F# `option` fields as not-required (removed from the `required` array) and nullable, matching what MCP clients expect.

## `FsMcpConfigException` -- error messages and how to fix them

`FsMcpConfigException` is raised at configuration time (not at runtime) when the server definition is invalid. Every message includes a hint about what to fix:

| Message pattern | Cause | Fix |
|---|---|---|
| `Server name is required...` | Missing `name` in `mcpServer { }` | Add `name "MyServer"` |
| `Server version is required...` | Missing `version` in `mcpServer { }` | Add `version "1.0.0"` |
| `Invalid server name...` | Empty or whitespace-only name | Use a non-empty name string |
| `Duplicate Tool: 'xyz'` | Two tools with the same name | Rename one or remove the duplicate |
| `Duplicate Resource: 'uri'` | Two resources with the same URI | Use unique URIs |
| `Duplicate Prompt: 'xyz'` | Two prompts with the same name | Rename one or remove the duplicate |
| `Tool name is required...` | Missing `toolName` in `mcpTool { }` | Add `toolName "myTool"` |
| `Tool handler is required...` | Missing `handler` or `typedHandler` in `mcpTool { }` | Add a handler |
| `Cannot add tool: ... already exists` | `DynamicServer.addTool` with duplicate name | Call `removeTool` first |

These exceptions are intentionally raised during configuration (at app startup) so you catch problems immediately, not on the first request.
