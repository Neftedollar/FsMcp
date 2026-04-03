module FsMcp.Server.Tests.TypedHandlerTests

open Expecto
open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

// ───────── Test arg types ─────────

type GreetArgs = { name: string; greeting: string option }
type MathArgs = { a: float; b: float }
type EmptyArgs = { placeholder: string option }

let unwrap r = match r with Ok v -> v | Error e -> failtest $"%A{e}"

[<Tests>]
let typedHandlerTests =
    testList "TypedHandlers" [
        testList "TypedTool.define" [
            testCase "creates tool with auto-generated schema" <| fun _ ->
                let td =
                    TypedTool.define<GreetArgs> "greet" "Greets a person" (fun args -> task {
                        let greeting = args.greeting |> Option.defaultValue "Hello"
                        return Ok [ Content.text $"{greeting}, {args.name}!" ]
                    }) |> unwrap
                Expect.equal (ToolName.value td.Name) "greet" "name"
                Expect.isSome td.InputSchema "has schema"

            testCase "schema marks required fields correctly" <| fun _ ->
                let td = TypedTool.define<GreetArgs> "t" "d" (fun _ -> Task.FromResult(Ok [])) |> unwrap
                let schema = td.InputSchema.Value
                // "name" should be required, "greeting" should not
                let required =
                    match schema.TryGetProperty("required") with
                    | true, arr -> arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq
                    | _ -> Set.empty
                Expect.isTrue (required.Contains "name") "name is required"
                Expect.isFalse (required.Contains "greeting") "greeting is optional"

            testCase "schema type is object (not [object, null])" <| fun _ ->
                let td = TypedTool.define<GreetArgs> "t" "d" (fun _ -> Task.FromResult(Ok [])) |> unwrap
                let schema = td.InputSchema.Value
                let typeVal = schema.GetProperty("type").GetString()
                Expect.equal typeVal "object" "type is plain object"

            testCase "handler deserializes args and invokes correctly" <| fun _ ->
                let td =
                    TypedTool.define<GreetArgs> "greet" "Greets" (fun args -> task {
                        let greeting = args.greeting |> Option.defaultValue "Hello"
                        return Ok [ Content.text $"{greeting}, {args.name}!" ]
                    }) |> unwrap
                let args = Map.ofList [
                    "name", JsonDocument.Parse("\"World\"").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.equal t "Hello, World!" "default greeting"
                | other -> failtest $"unexpected: %A{other}"

            testCase "handler passes optional args when provided" <| fun _ ->
                let td =
                    TypedTool.define<GreetArgs> "greet" "Greets" (fun args -> task {
                        let greeting = args.greeting |> Option.defaultValue "Hello"
                        return Ok [ Content.text $"{greeting}, {args.name}!" ]
                    }) |> unwrap
                let args = Map.ofList [
                    "name", JsonDocument.Parse("\"Alice\"").RootElement
                    "greeting", JsonDocument.Parse("\"Hi\"").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.equal t "Hi, Alice!" "custom greeting"
                | other -> failtest $"unexpected: %A{other}"

            testCase "handler with numeric args" <| fun _ ->
                let td =
                    TypedTool.define<MathArgs> "add" "Adds" (fun args -> task {
                        return Ok [ Content.text $"{args.a + args.b}" ]
                    }) |> unwrap
                let args = Map.ofList [
                    "a", JsonDocument.Parse("10.5").RootElement
                    "b", JsonDocument.Parse("20.3").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.equal t "30.8" "sum"
                | other -> failtest $"unexpected: %A{other}"

            testCase "handler returns error for invalid args" <| fun _ ->
                let td =
                    TypedTool.define<MathArgs> "add" "Adds" (fun args -> task {
                        return Ok [ Content.text "ok" ]
                    }) |> unwrap
                // Pass wrong type
                let args = Map.ofList [
                    "a", JsonDocument.Parse("\"not a number\"").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                Expect.isError result "invalid args produce error"

            testCase "returns error for empty tool name" <| fun _ ->
                let result = TypedTool.define<GreetArgs> "" "d" (fun _ -> Task.FromResult(Ok []))
                Expect.isError result "empty name"

            testCase "schema is cached (same reference on second call)" <| fun _ ->
                let td1 = TypedTool.define<GreetArgs> "t1" "d" (fun _ -> Task.FromResult(Ok [])) |> unwrap
                let td2 = TypedTool.define<GreetArgs> "t2" "d" (fun _ -> Task.FromResult(Ok [])) |> unwrap
                // Both should have schemas (caching ensures same generation)
                Expect.isSome td1.InputSchema "td1 has schema"
                Expect.isSome td2.InputSchema "td2 has schema"
                // Schema content should be identical
                let s1 = td1.InputSchema.Value.GetRawText()
                let s2 = td2.InputSchema.Value.GetRawText()
                Expect.equal s1 s2 "cached schema identical"
        ]

        testList "TypedPrompt.define" [
            testCase "creates prompt with auto-detected arguments" <| fun _ ->
                let pd =
                    TypedPrompt.define<GreetArgs> "greet" "Greets" (fun args -> task {
                        return Ok [ { Role = User; Content = Content.text $"Greet {args.name}" } ]
                    }) |> unwrap
                Expect.equal (PromptName.value pd.Name) "greet" "name"
                // "name" required, "greeting" optional
                let nameArg = pd.Arguments |> List.find (fun a -> a.Name = "name")
                let greetingArg = pd.Arguments |> List.find (fun a -> a.Name = "greeting")
                Expect.isTrue nameArg.Required "name required"
                Expect.isFalse greetingArg.Required "greeting optional"
        ]

        testList "mcpServer CE integration" [
            testCase "typed tools work in mcpServer CE" <| fun _ ->
                let config = mcpServer {
                    name "TypedServer"
                    version "1.0.0"
                    tool (TypedTool.define<GreetArgs> "greet" "Greets" (fun args -> task {
                        return Ok [ Content.text $"Hello, {args.name}!" ]
                    }) |> unwrap)
                    useStdio
                }
                Expect.equal (List.length config.Tools) 1 "one tool"
                Expect.isSome config.Tools.[0].InputSchema "has schema"
        ]
    ]
