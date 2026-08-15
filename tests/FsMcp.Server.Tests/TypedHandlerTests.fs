module FsMcp.Server.Tests.TypedHandlerTests

open Expecto
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open ModelContextProtocol

// ───────── Test arg types ─────────

type GreetArgs = { name: string; greeting: string option }
type MathArgs = { a: float; b: float }
type EmptyArgs = { placeholder: string option }
type ProtocolStringArgs = { count: int; enabled: bool }
type OptionalProtocolStringArgs = { count: int; enabled: bool option }

let unwrap r = match r with Ok v -> v | Error e -> failtest $"%A{e}"

[<Tests>]
let typedHandlerTests =
    testList "TypedHandlers" [
        testList "TypedTool.define" [
            testCase "creates tool with auto-generated schema" <| fun _ ->
                let td =
                    TypedTool.define<GreetArgs> "greet" "Greets a person" (fun args _ -> task {
                        let greeting = args.greeting |> Option.defaultValue "Hello"
                        return Ok [ Content.text $"{greeting}, {args.name}!" ]
                    }) |> unwrap
                Expect.equal (ToolName.value td.Name) "greet" "name"
                Expect.isSome td.InputSchema "has schema"

            testCase "schema marks required fields correctly" <| fun _ ->
                let td = TypedTool.define<GreetArgs> "t" "d" (fun _ _ -> Task.FromResult(Ok [])) |> unwrap
                let schema = td.InputSchema.Value
                // "name" should be required, "greeting" should not
                let required =
                    match schema.TryGetProperty("required") with
                    | true, arr -> arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq
                    | _ -> Set.empty
                Expect.isTrue (required.Contains "name") "name is required"
                Expect.isFalse (required.Contains "greeting") "greeting is optional"

            testCase "schema type is object (not [object, null])" <| fun _ ->
                let td = TypedTool.define<GreetArgs> "t" "d" (fun _ _ -> Task.FromResult(Ok [])) |> unwrap
                let schema = td.InputSchema.Value
                let typeVal = schema.GetProperty("type").GetString()
                Expect.equal typeVal "object" "type is plain object"

            testCase "handler deserializes args and invokes correctly" <| fun _ ->
                let td =
                    TypedTool.define<GreetArgs> "greet" "Greets" (fun args _ -> task {
                        let greeting = args.greeting |> Option.defaultValue "Hello"
                        return Ok [ Content.text $"{greeting}, {args.name}!" ]
                    }) |> unwrap
                let args = Map.ofList [
                    "name", JsonDocument.Parse("\"World\"").RootElement
                ]
                let result = td.Handler args CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.equal t "Hello, World!" "default greeting"
                | other -> failtest $"unexpected: %A{other}"

            testCase "handler passes optional args when provided" <| fun _ ->
                let td =
                    TypedTool.define<GreetArgs> "greet" "Greets" (fun args _ -> task {
                        let greeting = args.greeting |> Option.defaultValue "Hello"
                        return Ok [ Content.text $"{greeting}, {args.name}!" ]
                    }) |> unwrap
                let args = Map.ofList [
                    "name", JsonDocument.Parse("\"Alice\"").RootElement
                    "greeting", JsonDocument.Parse("\"Hi\"").RootElement
                ]
                let result = td.Handler args CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.equal t "Hi, Alice!" "custom greeting"
                | other -> failtest $"unexpected: %A{other}"

            testCase "handler with numeric args" <| fun _ ->
                let td =
                    TypedTool.define<MathArgs> "add" "Adds" (fun args _ -> task {
                        return Ok [ Content.text $"{args.a + args.b}" ]
                    }) |> unwrap
                let args = Map.ofList [
                    "a", JsonDocument.Parse("10.5").RootElement
                    "b", JsonDocument.Parse("20.3").RootElement
                ]
                let result = td.Handler args CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.equal t "30.8" "sum"
                | other -> failtest $"unexpected: %A{other}"

            testCase "handler raises redacted InvalidParams for invalid args" <| fun _ ->
                let td =
                    TypedTool.define<MathArgs> "add" "Adds" (fun _ _ -> task {
                        return Ok [ Content.text "ok" ]
                    }) |> unwrap
                let invalidValue = "not-a-number-secret"
                let args = Map.ofList [
                    "a", JsonDocument.Parse($"\"{invalidValue}\"").RootElement
                ]
                let error =
                    try
                        td.Handler args CancellationToken.None
                        |> fun pending -> pending.GetAwaiter().GetResult()
                        |> ignore
                        failtest "Expected invalid typed arguments to fail"
                    with :? McpProtocolException as caught -> caught
                Expect.equal error.ErrorCode McpErrorCode.InvalidParams "protocol error code"
                Expect.equal error.Message "Invalid tool arguments." "generic error message"
                Expect.isFalse (error.ToString().Contains invalidValue) "invalid value is redacted"

            testCase "handler JsonException remains HandlerException" <| fun _ ->
                let expected = JsonException("handler-owned-json-failure")
                let td =
                    TypedTool.define<GreetArgs> "greet" "Greets" (fun _ _ -> raise expected)
                    |> unwrap
                let args = Map.ofList [ "name", JsonDocument.Parse("\"World\"").RootElement ]
                let result =
                    td.Handler args CancellationToken.None
                    |> fun pending -> pending.GetAwaiter().GetResult()
                match result with
                | Error(HandlerException error) ->
                    Expect.isTrue
                        (obj.ReferenceEquals(error, expected))
                        "handler exception identity is preserved"
                | other -> failtestf "Expected HandlerException, got %A" other

            testCase "handler rejects coercible numeric and boolean strings without invocation" <| fun _ ->
                let mutable invocationCount = 0
                let td =
                    TypedTool.define<ProtocolStringArgs> "strict" "Strict JSON" (fun _ _ ->
                        invocationCount <- invocationCount + 1
                        Task.FromResult(Ok []))
                    |> unwrap
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
                        with :? McpProtocolException as caught -> caught
                    Expect.equal error.ErrorCode McpErrorCode.InvalidParams $"{caseName} code"
                    Expect.equal error.Message "Invalid tool arguments." $"{caseName} message"
                Expect.equal invocationCount 0 "strict deserialization rejects before the handler"

            testCase "returns error for empty tool name" <| fun _ ->
                let result = TypedTool.define<GreetArgs> "" "d" (fun _ _ -> Task.FromResult(Ok []))
                Expect.isError result "empty name"

            testCase "schema is cached (same reference on second call)" <| fun _ ->
                let td1 = TypedTool.define<GreetArgs> "t1" "d" (fun _ _ -> Task.FromResult(Ok [])) |> unwrap
                let td2 = TypedTool.define<GreetArgs> "t2" "d" (fun _ _ -> Task.FromResult(Ok [])) |> unwrap
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
                    TypedPrompt.define<GreetArgs> "greet" "Greets" (fun args _ -> task {
                        return Ok [ { Role = User; Content = Content.text $"Greet {args.name}" } ]
                    }) |> unwrap
                Expect.equal (PromptName.value pd.Name) "greet" "name"
                // "name" required, "greeting" optional
                let nameArg = pd.Arguments |> List.find (fun a -> a.Name = "name")
                let greetingArg = pd.Arguments |> List.find (fun a -> a.Name = "greeting")
                Expect.isTrue nameArg.Required "name required"
                Expect.isFalse greetingArg.Required "greeting optional"

            testCase "resource and prompt bind numeric and boolean protocol strings" <| fun _ ->
                let resource =
                    TypedResource.define<ProtocolStringArgs> "https://example.com/{count}/{enabled}" "typed" (fun args _ ->
                        let uri = ResourceUri.create $"https://example.com/{args.count}/{args.enabled}" |> unwrap
                        let mime = MimeType.create "text/plain" |> unwrap
                        Task.FromResult(Ok (TextResource(uri, mime, $"{args.count}:{args.enabled}"))))
                    |> unwrap
                let prompt =
                    TypedPrompt.define<ProtocolStringArgs> "typed" "typed" (fun args _ ->
                        Task.FromResult(Ok [ { Role = User; Content = Content.text $"{args.count}:{args.enabled}" } ]))
                    |> unwrap
                let arguments = Map.ofList [ "count", "17"; "enabled", "true" ]
                let resourceResult =
                    resource.Handler arguments CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                let promptResult =
                    prompt.Handler arguments CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                match resourceResult, promptResult with
                | Ok (TextResource(_, _, "17:True")), Ok [ { Content = Text "17:True" } ] -> ()
                | other -> failtest $"unexpected typed protocol binding: %A{other}"

            testCase "resource and prompt reject missing required fields without rejecting options" <| fun _ ->
                let mutable requiredResourceInvoked = false
                let mutable requiredPromptInvoked = false
                let mime = MimeType.create "text/plain" |> unwrap
                let uri = ResourceUri.create "https://example.com/17" |> unwrap
                let requiredResource =
                    TypedResource.define<ProtocolStringArgs>
                        "https://example.com/{count}/{enabled}"
                        "required"
                        (fun _ _ ->
                            requiredResourceInvoked <- true
                            Task.FromResult(Ok (TextResource(uri, mime, "unexpected"))))
                    |> unwrap
                let requiredPrompt =
                    TypedPrompt.define<ProtocolStringArgs> "required" "required" (fun _ _ ->
                        requiredPromptInvoked <- true
                        Task.FromResult(Ok []))
                    |> unwrap
                let optionalResource =
                    TypedResource.define<OptionalProtocolStringArgs>
                        "https://example.com/{count}"
                        "optional"
                        (fun args _ ->
                            Task.FromResult(
                                Ok (TextResource(uri, mime, $"{args.count}:{Option.isNone args.enabled}"))))
                    |> unwrap
                let optionalPrompt =
                    TypedPrompt.define<OptionalProtocolStringArgs> "optional" "optional" (fun args _ ->
                        Task.FromResult(
                            Ok [ { Role = User; Content = Content.text $"{args.count}:{Option.isNone args.enabled}" } ]))
                    |> unwrap
                let missingRequired = Map.ofList [ "count", "17" ]
                let requiredResourceResult =
                    requiredResource.Handler missingRequired CancellationToken.None
                    |> fun pending -> pending.GetAwaiter().GetResult()
                let requiredPromptResult =
                    requiredPrompt.Handler missingRequired CancellationToken.None
                    |> fun pending -> pending.GetAwaiter().GetResult()
                let optionalResourceResult =
                    optionalResource.Handler missingRequired CancellationToken.None
                    |> fun pending -> pending.GetAwaiter().GetResult()
                let optionalPromptResult =
                    optionalPrompt.Handler missingRequired CancellationToken.None
                    |> fun pending -> pending.GetAwaiter().GetResult()
                let expectInvalidParams primitive = function
                    | Error(ProtocolError(code, message)) ->
                        Expect.equal code (int McpErrorCode.InvalidParams) $"{primitive} code"
                        Expect.equal message $"Invalid {primitive} arguments." $"{primitive} message"
                    | result -> failtestf "Expected redacted %s InvalidParams, got %A" primitive result
                requiredResourceResult |> expectInvalidParams "resource"
                requiredPromptResult |> expectInvalidParams "prompt"
                Expect.isFalse requiredResourceInvoked "required resource handler is not invoked"
                Expect.isFalse requiredPromptInvoked "required prompt handler is not invoked"
                match optionalResourceResult, optionalPromptResult with
                | Ok (TextResource(_, _, "17:True")), Ok [ { Content = Text "17:True" } ] -> ()
                | results -> failtestf "Optional typed fields should remain optional, got %A" results

            testCase "typed tool resource and prompt preserve cancellation" <| fun _ ->
                use cancellation = new CancellationTokenSource()
                cancellation.Cancel()
                let tool =
                    TypedTool.define<GreetArgs> "cancel-tool" "cancel" (fun _ token ->
                        Task.FromCanceled<Result<Content list, McpError>>(token))
                    |> unwrap
                let resource =
                    TypedResource.define<ProtocolStringArgs> "https://example.com/cancel/{count}/{enabled}" "cancel" (fun _ token ->
                        Task.FromCanceled<Result<ResourceContents, McpError>>(token))
                    |> unwrap
                let prompt =
                    TypedPrompt.define<ProtocolStringArgs> "cancel-prompt" "cancel" (fun _ token ->
                        Task.FromCanceled<Result<McpMessage list, McpError>>(token))
                    |> unwrap
                let toolArgs = Map.ofList [ "name", JsonDocument.Parse("\"test\"").RootElement ]
                let stringArgs = Map.ofList [ "count", "1"; "enabled", "false" ]
                let isCancelled (pending: Task<'T>) =
                    try
                        pending.GetAwaiter().GetResult() |> ignore
                        false
                    with :? System.OperationCanceledException -> true
                Expect.isTrue (isCancelled (tool.Handler toolArgs cancellation.Token)) "tool cancellation"
                Expect.isTrue (isCancelled (resource.Handler stringArgs cancellation.Token)) "resource cancellation"
                Expect.isTrue (isCancelled (prompt.Handler stringArgs cancellation.Token)) "prompt cancellation"
        ]

        testList "mcpServer CE integration" [
            testCase "typed tools work in mcpServer CE" <| fun _ ->
                let config = mcpServer {
                    name "TypedServer"
                    version "1.0.0"
                    tool (TypedTool.define<GreetArgs> "greet" "Greets" (fun args _ -> task {
                        return Ok [ Content.text $"Hello, {args.name}!" ]
                    }) |> unwrap)
                }
                Expect.equal (List.length config.Tools) 1 "one tool"
                Expect.isSome config.Tools.[0].InputSchema "has schema"
        ]
    ]
