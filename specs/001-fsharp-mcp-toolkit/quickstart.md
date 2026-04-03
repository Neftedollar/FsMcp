# Quickstart: FsMcp

## Prerequisites

- .NET 10 SDK (or later)

## 1. Create a new project

```bash
dotnet new console -lang F# -o MyMcpServer
cd MyMcpServer
dotnet add reference ../src/FsMcp.Core/FsMcp.Core.fsproj
dotnet add reference ../src/FsMcp.Server/FsMcp.Server.fsproj
```

## 2. Define an MCP server with a tool

```fsharp
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

[<EntryPoint>]
let main _ =
    let server = mcpServer {
        name "My Server"
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
            |> Result.defaultWith (fun e -> failwith $"%A{e}"))

        useStdio
    }

    Server.run server |> fun t -> t.GetAwaiter().GetResult()
    0
```

## 3. Run it

```bash
dotnet run
```

The server listens on stdio. Connect with Claude Desktop, VS Code, or any MCP client.

## 4. Run over HTTP instead

```fsharp
Server.runHttp server (Some "/mcp") "http://localhost:3001"
|> fun t -> t.GetAwaiter().GetResult()
```

## 5. Connect as a client (from another F# project)

```fsharp
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Client

let demo () = task {
    let config = {
        Transport = ClientTransport.stdio "dotnet" ["run"; "--project"; "../MyMcpServer"]
        Name = "Test Client"
        ShutdownTimeout = None
    }

    let! client = McpClient.connect config
    let! tools = McpClient.listTools client
    for t in tools do printfn "Tool: %s" t.Name

    let toolName = ToolName.create "greet" |> Result.defaultWith failwith
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

## 6. Test your handlers without transport

```fsharp
open FsMcp.Testing

// Direct handler invocation — no network, no process spawning
let result =
    TestServer.callTool serverConfig "greet"
        (Map.ofList ["name", System.Text.Json.JsonDocument.Parse("\"World\"").RootElement])
    |> Async.AwaitTask |> Async.RunSynchronously

// Assertion helpers
result |> Expect.mcpHasTextContent "Hello, World!" "greet works"
```

## 7. Run tests

```bash
dotnet test
```

## Validation checklist

- [ ] Server starts and accepts MCP connections via stdio
- [ ] Server starts and accepts MCP connections via HTTP
- [ ] `tools/list` returns the registered tool
- [ ] `tools/call` with `{"name":"FsMcp"}` returns `"Hello, FsMcp!"`
- [ ] Client connects, lists tools, calls tool, gets typed result
- [ ] TestServer.callTool invokes handler directly
- [ ] All 227 tests pass via `dotnet test`
