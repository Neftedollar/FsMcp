---
title: Testing Guide
category: Guides
categoryindex: 1
index: 0
---

# Testing Guide

FsMcp ships a dedicated testing package that lets you test handlers directly against a `ServerConfig` without spinning up any transport.

```bash
dotnet add package FsMcp.Testing
```

`FsMcp.Testing` depends on Expecto and FsCheck.

## `TestServer` -- test handlers without transport

`TestServer` invokes tool, resource, and prompt handlers by looking them up in the `ServerConfig` and calling them directly:

### `TestServer.callTool`

```fsharp
open System.Text.Json
open FsMcp.Core
open FsMcp.Server
open FsMcp.Testing

let config = mcpServer {
    name "TestServer"
    version "1.0.0"
    tool (Tool.define "echo" "Echoes input" (fun args ->
        let msg =
            args
            |> Map.tryFind "message"
            |> Option.map (fun j -> j.GetString())
            |> Option.defaultValue "(none)"
        task { return Ok [ Content.text $"Echo: {msg}" ] })
    |> unwrapResult)
    useStdio
}

// Call the tool
let result =
    TestServer.callTool config "echo" (Map.ofList [
        "message", JsonDocument.Parse("\"hello\"").RootElement
    ])
    |> Async.AwaitTask |> Async.RunSynchronously

// result : Result<Content list, McpError>
```

Returns `Error (ToolNotFound tn)` if the tool name does not exist in the config.

### `TestServer.readResource`

```fsharp
let result =
    TestServer.readResource config "file:///tmp/test.txt" Map.empty
    |> Async.AwaitTask |> Async.RunSynchronously

// result : Result<ResourceContents, McpError>
```

Returns `Error (ResourceNotFound ru)` if the URI is not registered.

### `TestServer.getPrompt`

```fsharp
let result =
    TestServer.getPrompt config "summarize" (Map.ofList [ "topic", "testing" ])
    |> Async.AwaitTask |> Async.RunSynchronously

// result : Result<McpMessage list, McpError>
```

Returns `Error (PromptNotFound pn)` if the prompt name is not registered.

### `TestServer.listTools`, `listResources`, `listPrompts`

These return simple info records without invoking any handlers:

```fsharp
let tools : FsMcp.Testing.ToolInfo list = TestServer.listTools config
let resources : FsMcp.Testing.ResourceInfo list = TestServer.listResources config
let prompts : FsMcp.Testing.PromptInfo list = TestServer.listPrompts config
```

## Assertion helpers in `Expect`

The `FsMcp.Testing.Expect` module provides MCP-specific assertions that produce clear failure messages:

### `Expect.mcpIsSuccess`

Assert that a `Result` is `Ok` and return the inner value:

```fsharp
let contents = Expect.mcpIsSuccess "tool should succeed" result
// contents : Content list
```

Throws with the `McpError` details if the result is `Error`.

### `Expect.mcpIsError`

Assert that a `Result` is `Error` and return the `McpError`:

```fsharp
let err = Expect.mcpIsError "should fail for missing tool" result
// err : McpError
```

### `Expect.mcpHasTextContent`

Assert that a `Result<Content list, McpError>` is `Ok` and contains at least one `Text` item matching the expected substring:

```fsharp
Expect.mcpHasTextContent "7" "add result" result
```

This is a convenience that combines `mcpIsSuccess` + `mcpContainsText`.

### `Expect.mcpContainsText`

Assert that a `Content list` contains at least one `Text` item matching a substring:

```fsharp
let contents = Expect.mcpIsSuccess "should succeed" result
Expect.mcpContainsText "hello" "greeting text" contents
```

### `Expect.mcpHasContentCount`

Assert the number of content items:

```fsharp
Expect.mcpHasContentCount 1 "single result" contents
```

## `McpArbitraries` -- FsCheck generators for property testing

The `McpArbitraries` module provides `Arbitrary` values for all domain types:

| Arbitrary | Type |
|---|---|
| `McpArbitraries.toolName` | `Arbitrary<ToolName>` |
| `McpArbitraries.resourceUri` | `Arbitrary<ResourceUri>` |
| `McpArbitraries.promptName` | `Arbitrary<PromptName>` |
| `McpArbitraries.mimeType` | `Arbitrary<MimeType>` |
| `McpArbitraries.content` | `Arbitrary<Content>` |
| `McpArbitraries.resourceContents` | `Arbitrary<ResourceContents>` |
| `McpArbitraries.mcpMessage` | `Arbitrary<McpMessage>` |
| `McpArbitraries.toolCallArgs` | `Arbitrary<JsonElement>` |

### Register all arbitraries globally

```fsharp
McpArbitraries.register ()
```

This calls `Arb.register<McpArbitraryProvider>()` so FsCheck picks them up automatically.

### Use with Expecto's FsCheck integration

```fsharp
open Expecto
open FsCheck
open FsMcp.Testing

let propertyTests =
    testList "Property tests" [
        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with arbitrary = [ typeof<McpArbitraries.McpArbitraryProvider> ] }
            "ToolName roundtrips through value"
            (fun (tn: FsMcp.Core.Validation.ToolName) ->
                let s = FsMcp.Core.Validation.ToolName.value tn
                let rt = FsMcp.Core.Validation.ToolName.create s |> unwrapResult
                FsMcp.Core.Validation.ToolName.value rt = s)
    ]
```

## Full example: test a calculator server

```fsharp
module CalculatorTests

open Expecto
open System.Text.Json
open FsMcp.Core
open FsMcp.Server
open FsMcp.Testing

type CalcArgs = { a: float; b: float }

let calcServer = mcpServer {
    name "Calculator"
    version "1.0.0"

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args -> task {
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    tool (TypedTool.define<CalcArgs> "divide" "Divide a by b" (fun args -> task {
        if args.b = 0.0 then
            return Error (TransportError "Division by zero")
        else
            return Ok [ Content.text $"{args.a / args.b}" ]
    }) |> unwrapResult)

    useStdio
}

let jsonEl (s: string) = JsonDocument.Parse(s).RootElement

[<Tests>]
let tests =
    testList "Calculator" [

        testCase "add returns correct sum" <| fun _ ->
            let result =
                TestServer.callTool calcServer "add"
                    (Map.ofList [ "a", jsonEl "3"; "b", jsonEl "4" ])
                |> Async.AwaitTask |> Async.RunSynchronously
            Expect.mcpHasTextContent "7" "3 + 4 = 7" result

        testCase "divide returns correct quotient" <| fun _ ->
            let result =
                TestServer.callTool calcServer "divide"
                    (Map.ofList [ "a", jsonEl "10"; "b", jsonEl "2" ])
                |> Async.AwaitTask |> Async.RunSynchronously
            Expect.mcpHasTextContent "5" "10 / 2 = 5" result

        testCase "divide by zero returns error" <| fun _ ->
            let result =
                TestServer.callTool calcServer "divide"
                    (Map.ofList [ "a", jsonEl "1"; "b", jsonEl "0" ])
                |> Async.AwaitTask |> Async.RunSynchronously
            let err = Expect.mcpIsError "should fail" result
            match err with
            | TransportError msg ->
                Expect.equal msg "Division by zero" "error message"
            | other ->
                failtest $"unexpected error: %A{other}"

        testCase "unknown tool returns ToolNotFound" <| fun _ ->
            let result =
                TestServer.callTool calcServer "sqrt" Map.empty
                |> Async.AwaitTask |> Async.RunSynchronously
            let err = Expect.mcpIsError "should not find sqrt" result
            match err with
            | ToolNotFound _ -> ()
            | other -> failtest $"expected ToolNotFound, got %A{other}"

        testCase "listTools returns both tools" <| fun _ ->
            let tools = TestServer.listTools calcServer
            Expect.equal (List.length tools) 2 "two tools"
            let names = tools |> List.map (fun t -> t.Name) |> List.sort
            Expect.equal names [ "add"; "divide" ] "tool names"
    ]
```

Run with:

```bash
dotnet test
```
