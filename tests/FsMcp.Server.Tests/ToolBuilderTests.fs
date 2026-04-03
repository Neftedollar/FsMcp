module FsMcp.Server.Tests.ToolBuilderTests

open Expecto
open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

// ───────── Test arg types ─────────

type GreetArgs = { name: string; greeting: string option }

[<Tests>]
let toolBuilderTests =
    testList "ToolBuilder" [
        testCase "mcpTool CE creates valid ToolDefinition" <| fun _ ->
            let td : ToolDefinition = mcpTool {
                toolName "echo"
                description "Echoes input"
                handler (fun (_args: Map<string, JsonElement>) -> task {
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
                typedHandler (TypedHandler.create<GreetArgs> (fun args -> task {
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
                    typedHandler (TypedHandler.create<GreetArgs> (fun args -> task {
                        return Ok [ Content.text $"Hello, {args.name}!" ]
                    }))
                })
                useStdio
            }
            Expect.equal (List.length config.Tools) 1 "one tool"
            Expect.equal (ToolName.value config.Tools.[0].Name) "greet" "tool name"
            Expect.isSome config.Tools.[0].InputSchema "has schema"

        testCase "mcpTool handler invocation works" <| fun _ ->
            let td : ToolDefinition = mcpTool {
                toolName "greet"
                description "Greets"
                typedHandler (TypedHandler.create<GreetArgs> (fun args -> task {
                    let greeting = args.greeting |> Option.defaultValue "Hello"
                    return Ok [ Content.text $"{greeting}, {args.name}!" ]
                }))
            }
            let args = Map.ofList [
                "name", JsonDocument.Parse("\"World\"").RootElement
            ]
            let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Ok [ Text t ] -> Expect.equal t "Hello, World!" "default greeting"
            | other -> failtest $"unexpected: %A{other}"

        testCase "mcpTool fails if name missing" <| fun _ ->
            Expect.throwsT<System.Exception>
                (fun () ->
                    let _td : ToolDefinition = mcpTool {
                        description "No name"
                        handler (fun (_args: Map<string, JsonElement>) -> Task.FromResult(Ok []))
                    }
                    ())
                "missing name should fail"

        testCase "mcpTool fails if handler missing" <| fun _ ->
            Expect.throwsT<System.Exception>
                (fun () ->
                    let _td : ToolDefinition = mcpTool {
                        toolName "orphan"
                        description "No handler"
                    }
                    ())
                "missing handler should fail"
    ]
