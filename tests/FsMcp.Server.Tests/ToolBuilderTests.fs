module FsMcp.Server.Tests.ToolBuilderTests

open Expecto
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

// ───────── Test arg types ─────────

type GreetArgs = { name: string; greeting: string option }
type StrictArgs = { count: int; enabled: bool }

[<Tests>]
let toolBuilderTests =
    testList "ToolBuilder" [
        testCase "mcpTool CE creates valid ToolDefinition" <| fun _ ->
            let td : ToolDefinition = mcpTool {
                toolName "echo"
                description "Echoes input"
                handler (fun (_args: Map<string, JsonElement>) _ -> task {
                    return Ok [ Content.text "echo" ]
                })
            }
            Expect.equal (ToolName.value td.Name) "echo" "tool name"
            Expect.equal td.Description "Echoes input" "description"
            Expect.isNone td.InputSchema "no schema for raw handler"

        testCase "mcpTool with typedHandler generates schema" <| fun _ ->
            let td : ToolDefinition = mcpTool {
                toolName "greet"
                description "Greets a person"
                typedHandler (TypedHandler.create<GreetArgs> (fun args _ -> task {
                    return Ok [ Content.text $"Hello, {args.name}!" ]
                }))
            }
            Expect.equal (ToolName.value td.Name) "greet" "tool name"
            Expect.isSome td.InputSchema "has schema"
            let schema = td.InputSchema.Value
            // Schema should have "properties" with "name" and "greeting"
            let props = schema.GetProperty("properties")
            Expect.isTrue (props.TryGetProperty("name") |> fst) "has name property"
            Expect.isTrue (props.TryGetProperty("greeting") |> fst) "has greeting property"
            // "name" should be required, "greeting" should not
            let required =
                match schema.TryGetProperty("required") with
                | true, arr -> arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq
                | _ -> Set.empty
            Expect.isTrue (required.Contains "name") "name is required"
            Expect.isFalse (required.Contains "greeting") "greeting is optional"

        testCase "mcpTool works inside mcpServer CE" <| fun _ ->
            let config = mcpServer {
                name "TestServer"
                version "1.0.0"
                tool (mcpTool {
                    toolName "greet"
                    description "Greets a person"
                    typedHandler (TypedHandler.create<GreetArgs> (fun args _ -> task {
                        return Ok [ Content.text $"Hello, {args.name}!" ]
                    }))
                })
            }
            Expect.equal (List.length config.Tools) 1 "one tool"
            Expect.equal (ToolName.value config.Tools.[0].Name) "greet" "tool name"
            Expect.isSome config.Tools.[0].InputSchema "has schema"

        testCase "mcpTool handler invocation works" <| fun _ ->
            let td : ToolDefinition = mcpTool {
                toolName "greet"
                description "Greets"
                typedHandler (TypedHandler.create<GreetArgs> (fun args _ -> task {
                    let greeting = args.greeting |> Option.defaultValue "Hello"
                    return Ok [ Content.text $"{greeting}, {args.name}!" ]
                }))
            }
            let args = Map.ofList [
                "name", JsonDocument.Parse("\"World\"").RootElement
            ]
            let result = td.Handler args CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Ok [ Text t ] -> Expect.equal t "Hello, World!" "default greeting"
            | other -> failtest $"unexpected: %A{other}"

        testCase "TypedHandler.create preserves cancellation" <| fun _ ->
            let td : ToolDefinition = mcpTool {
                toolName "cancel"
                typedHandler (TypedHandler.create<GreetArgs> (fun _ cancellationToken ->
                    Task.FromCanceled<Result<Content list, McpError>>(cancellationToken)))
            }
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()
            let args = Map.ofList [ "name", JsonDocument.Parse("\"test\"").RootElement ]
            let cancelled =
                try
                    td.Handler args cancellation.Token
                    |> fun pending -> pending.GetAwaiter().GetResult()
                    |> ignore
                    false
                with :? System.OperationCanceledException -> true
            Expect.isTrue cancelled "typed CE cancellation is not wrapped"

        testCase "typedHandler rejects coercible JSON strings without invocation" <| fun _ ->
            let mutable invocationCount = 0
            let td : ToolDefinition = mcpTool {
                toolName "strict"
                typedHandler (
                    TypedHandler.create<StrictArgs> (fun _ _ ->
                        invocationCount <- invocationCount + 1
                        Task.FromResult(Ok [])))
            }
            let cases = [
                "numeric string",
                Map.ofList [
                    "count", JsonSerializer.SerializeToElement "42"
                    "enabled", JsonSerializer.SerializeToElement true
                ]
                "boolean string",
                Map.ofList [
                    "count", JsonSerializer.SerializeToElement 42
                    "enabled", JsonSerializer.SerializeToElement "true"
                ]
            ]
            for caseName, arguments in cases do
                let error =
                    try
                        td.Handler arguments CancellationToken.None
                        |> fun pending -> pending.GetAwaiter().GetResult()
                        |> ignore
                        failtestf "Expected %s to fail" caseName
                    with :? ModelContextProtocol.McpProtocolException as caught -> caught
                Expect.equal
                    error.ErrorCode
                    ModelContextProtocol.McpErrorCode.InvalidParams
                    $"{caseName} code"
                Expect.equal error.Message "Invalid tool arguments." $"{caseName} message"
            Expect.equal invocationCount 0 "strict deserialization rejects before the CE handler"

        testCase "mcpTool fails if name missing" <| fun _ ->
            Expect.throws
                (fun () ->
                    let _td : ToolDefinition = mcpTool {
                        description "No name"
                        handler (fun (_args: Map<string, JsonElement>) _ -> Task.FromResult(Ok []))
                    }
                    ())
                "missing name should fail"

        testCase "mcpTool fails if handler missing" <| fun _ ->
            Expect.throws
                (fun () ->
                    let _td : ToolDefinition = mcpTool {
                        toolName "orphan"
                        description "No handler"
                    }
                    ())
                "missing handler should fail"
    ]
