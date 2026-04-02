# Quickstart: FsMcp

## Prerequisites

- .NET 8 SDK or later
- F# (included with .NET SDK)

## 1. Create a new project

```bash
dotnet new console -lang F# -o MyMcpServer
cd MyMcpServer
dotnet add reference ../src/FsMcp.Core/FsMcp.Core.fsproj
dotnet add reference ../src/FsMcp.Server/FsMcp.Server.fsproj
```

## 2. Define an MCP server with a tool

```fsharp
open FsMcp.Types
open FsMcp.Server.Builder

[<EntryPoint>]
let main argv =
    let server = mcpServer {
        name "My Server"
        version "1.0.0"

        tool (
            Tool.define
                "greet"
                "Greets a person by name"
                (fun args -> task {
                    let name =
                        args
                        |> Map.tryFind "name"
                        |> Option.map (fun j -> j.GetString())
                        |> Option.defaultValue "World"
                    return Ok [ Content.text $"Hello, {name}!" ]
                })
            |> Result.defaultWith (fun e -> failwith $"Invalid tool: %A{e}")
        )

        useStdio
    }

    server |> Server.run |> fun t -> t.GetAwaiter().GetResult()
    0
```

## 3. Run it

```bash
dotnet run
```

The server is now listening on stdio. Connect with any MCP client
(e.g., Claude Desktop, VS Code MCP extension).

## 4. Connect as a client (from another F# project)

```fsharp
open FsMcp.Types
open FsMcp.Client

let demo () = task {
    let config = {
        Transport = Transport.stdio "dotnet" ["run"; "--project"; "../MyMcpServer"]
        Name = "Test Client"
        ShutdownTimeout = None
    }

    let! client = Client.connect config
    let! tools = Client.listTools client
    printfn "Available tools: %A" (tools |> List.map (fun t -> ToolName.value t.Name))

    let toolName = ToolName.create "greet" |> Result.defaultWith failwith
    let! result = Client.callTool client toolName (Map.ofList ["name", box "FsMcp"])

    match result with
    | Ok contents -> contents |> List.iter (fun c -> printfn "%A" c)
    | Error err -> printfn "Error: %A" err

    do! Client.disconnect client
}
```

## 5. Run tests

```bash
dotnet test
```

All Expecto + FsCheck tests run via `dotnet test`.

## Validation checklist

- [ ] Server starts and accepts MCP connections
- [ ] `tools/list` returns the registered tool
- [ ] `tools/call` with `{"name":"FsMcp"}` returns `"Hello, FsMcp!"`
- [ ] Client connects, lists tools, calls tool, gets typed result
- [ ] All tests pass via `dotnet test`
