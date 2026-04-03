---
sidebar_position: 5
description: "Connect to MCP servers from F# using McpClient, McpClientAsync, and the pipe-friendly ClientPipeline with Result-based error handling."
---


# Client Guide

## ClientTransport

`ClientTransport` is a DU describing how to connect to an MCP server:

```fsharp
type ClientTransport =
    | StdioProcess of command: string * args: string list
    | HttpEndpoint of uri: Uri * headers: Map<string, string>
```

Use the convenience constructors in the `ClientTransport` module:

```fsharp
open FsMcp.Client

// Launch a server as a child process over stdio
let stdio = ClientTransport.stdio "dotnet" [ "run"; "--project"; "./MyServer" ]

// Connect to an HTTP server
let http = ClientTransport.http "http://localhost:5000/mcp"

// HTTP with auth headers
let httpAuth =
    ClientTransport.httpWithHeaders
        "http://localhost:5000/mcp"
        (Map.ofList [ "Authorization", "Bearer my-token" ])
```

## Connecting with `McpClient`

Create a `ClientConfig` and call `McpClient.connect`:

```fsharp
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Client

let config : ClientConfig = {
    Transport = ClientTransport.stdio "dotnet" [ "run"; "--project"; "./MyServer" ]
    Name = "my-client"
    ShutdownTimeout = None
}

// Task-based
task {
    let! client = McpClient.connect config

    // List tools
    let! tools = McpClient.listTools client
    for t in tools do
        printfn $"Tool: {t.Name} -- {t.Description}"

    // Call a tool
    let toolName = ToolName.create "greet" |> unwrapResult
    let args = Map.ofList [
        "name", System.Text.Json.JsonDocument.Parse("\"World\"").RootElement
    ]
    let! result = McpClient.callTool client toolName args
    match result with
    | Ok contents ->
        for c in contents do
            match c with
            | Text t -> printfn $"Result: {t}"
            | _ -> ()
    | Error err -> printfn $"Error: %A{err}"

    // Read a resource
    let uri = ResourceUri.create "info://server/status" |> unwrapResult
    let! resource = McpClient.readResource client uri
    match resource with
    | Ok (TextResource (_, _, text)) -> printfn $"Resource: {text}"
    | Ok (BlobResource _) -> printfn "Got binary resource"
    | Error err -> printfn $"Error: %A{err}"

    // List and get a prompt
    let! prompts = McpClient.listPrompts client
    let promptName = PromptName.create "summarize" |> unwrapResult
    let! promptResult = McpClient.getPrompt client promptName (Map.ofList [ "topic", "F#" ])
    match promptResult with
    | Ok messages ->
        for m in messages do
            match m.Content with
            | Text t -> printfn $"[{m.Role}] {t}"
            | _ -> ()
    | Error err -> printfn $"Error: %A{err}"

    // Disconnect
    do! McpClient.disconnect client
}
```

## Return types

All operations return F# domain types:

| Function | Return type |
|---|---|
| `McpClient.connect` | `Task<McpClient>` |
| `McpClient.listTools` | `Task<ToolInfo list>` |
| `McpClient.callTool` | `Task<Result<Content list, McpError>>` |
| `McpClient.listResources` | `Task<ResourceInfo list>` |
| `McpClient.readResource` | `Task<Result<ResourceContents, McpError>>` |
| `McpClient.listPrompts` | `Task<PromptInfo list>` |
| `McpClient.getPrompt` | `Task<Result<McpMessage list, McpError>>` |
| `McpClient.disconnect` | `Task<unit>` |

`ToolInfo`, `ResourceInfo`, and `PromptInfo` are simplified record types with string fields for easy consumption.

## `McpClientAsync` module

Every function in `McpClient` has an `Async` counterpart in `McpClientAsync`:

```fsharp
open FsMcp.Client

async {
    let! client = McpClientAsync.connect config
    let! tools = McpClientAsync.listTools client
    let! result = McpClientAsync.callTool client toolName args
    do! McpClientAsync.disconnect client
}
```

These are thin wrappers that call `Async.AwaitTask` on the `Task`-based versions.

## `ClientPipeline` (FsMcp.TaskApi)

The `FsMcp.TaskApi` package provides pipe-friendly operations for use with `taskResult { }` from FsToolkit.ErrorHandling. Every operation validates string inputs internally and returns `Task<Result<'T, McpError>>`:

```bash
dotnet add package FsMcp.TaskApi
```

```fsharp
open FsMcp.Core
open FsMcp.Client
open FsMcp.TaskApi
open FsToolkit.ErrorHandling

let config : ClientConfig = {
    Transport = ClientTransport.stdio "dotnet" [ "run"; "--project"; "./MyServer" ]
    Name = "pipeline-client"
    ShutdownTimeout = None
}

let run () =
    taskResult {
        let! client = ClientPipeline.connect config

        // List tools (Result-wrapped)
        let! tools = ClientPipeline.listTools client

        // Call a tool by string name -- validates name internally
        let args = Map.ofList [
            "name", System.Text.Json.JsonDocument.Parse("\"World\"").RootElement
        ]
        let! contents = client |> ClientPipeline.callTool "greet" args

        // Shortcut: call tool and extract the first text content
        let! text = client |> ClientPipeline.callToolText "greet" args
        printfn $"Got: {text}"

        // Read a resource by string URI
        let! resource = client |> ClientPipeline.readResource "info://server/status"

        // Get a prompt by string name
        let! messages = client |> ClientPipeline.getPrompt "summarize" (Map.ofList [ "topic", "MCP" ])

        // List resources and prompts
        let! resources = ClientPipeline.listResources client
        let! prompts = ClientPipeline.listPrompts client

        do! client |> ClientPipeline.disconnect
    }
```

Notice the pipe-friendly argument order: `client` is the last parameter in `callTool`, `callToolText`, `readResource`, `getPrompt`, and `disconnect`, enabling `client |> ClientPipeline.callTool "name" args`.

## Error handling with `Result<'T, McpError>`

All fallible operations return `Result`. The `McpError` DU covers every failure mode:

```fsharp
type McpError =
    | ValidationFailed of errors: ValidationError list
    | ToolNotFound of name: ToolName
    | ResourceNotFound of uri: ResourceUri
    | PromptNotFound of name: PromptName
    | HandlerException of exn: exn
    | TransportError of message: string
    | ProtocolError of code: int * message: string
```

Pattern match to handle specific errors:

```fsharp
match result with
| Ok contents -> // handle success
| Error (ToolNotFound tn) ->
    printfn $"Tool '{ToolName.value tn}' not found"
| Error (TransportError msg) ->
    printfn $"Transport error: {msg}"
| Error (ProtocolError (code, msg)) ->
    printfn $"Protocol error {code}: {msg}"
| Error (HandlerException ex) ->
    printfn $"Handler threw: {ex.Message}"
| Error (ValidationFailed errors) ->
    for e in errors do
        printfn $"Validation: {ValidationError.format e}"
| Error err ->
    printfn $"Other error: %A{err}"
```
