module FsMcp.Sampling.Tests.SamplingTests

open Expecto
open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Sampling

type SummarizeArgs = { text: string; maxLength: int option }

[<Tests>]
let samplingTests =
    testList "Sampling" [
        testList "SamplingRequest" [
            testCase "simple creates request with one user message" <| fun _ ->
                let req = SamplingRequest.simple "Hello" 100
                Expect.equal (List.length req.Messages) 1 "one message"
                Expect.equal req.Messages.[0].Role User "user role"
                Expect.equal req.MaxTokens 100 "max tokens"
                Expect.isNone req.SystemPrompt "no system"
                Expect.isNone req.Temperature "no temperature"

            testCase "withSystem sets system prompt" <| fun _ ->
                let req = SamplingRequest.withSystem "You are helpful" "Hi" 50
                Expect.equal req.SystemPrompt (Some "You are helpful") "system set"

            testCase "withTemperature sets temperature" <| fun _ ->
                let req = SamplingRequest.simple "Hi" 100 |> SamplingRequest.withTemperature 0.7
                Expect.equal req.Temperature (Some 0.7) "temp set"

            testCase "withModel sets model hint" <| fun _ ->
                let req = SamplingRequest.simple "Hi" 100 |> SamplingRequest.withModel "claude-3"
                Expect.equal req.ModelHint (Some "claude-3") "model set"

            testCase "withStopSequences sets stop sequences" <| fun _ ->
                let req = SamplingRequest.simple "Hi" 100 |> SamplingRequest.withStopSequences ["STOP"; "END"]
                Expect.equal req.StopSequences ["STOP"; "END"] "stops set"

            testCase "builders compose via pipe" <| fun _ ->
                let req =
                    SamplingRequest.simple "Analyze this" 500
                    |> SamplingRequest.withTemperature 0.3
                    |> SamplingRequest.withModel "claude-opus"
                    |> SamplingRequest.withStopSequences ["---"]
                Expect.equal req.MaxTokens 500 "tokens"
                Expect.equal req.Temperature (Some 0.3) "temp"
                Expect.equal req.ModelHint (Some "claude-opus") "model"
        ]

        testList "SamplingTool" [
            testCase "noOpSample returns SamplingNotSupported" <| fun _ ->
                let result =
                    SamplingTool.noOpSample (SamplingRequest.simple "test" 10)
                    |> Async.AwaitTask |> Async.RunSynchronously
                Expect.equal result (Error SamplingNotSupported) "not supported"

            testCase "mockSample returns fixed response" <| fun _ ->
                let result =
                    SamplingTool.mockSample "mocked answer" (SamplingRequest.simple "test" 10)
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok r ->
                    Expect.equal r.Model "mock" "mock model"
                    match r.Message.Content with
                    | Text t -> Expect.equal t "mocked answer" "mocked text"
                    | _ -> failtest "expected text"
                | Error e -> failtest $"unexpected error: %A{e}"

            testCase "noOpContext creates context with noOp sample" <| fun _ ->
                let ctx = SamplingTool.noOpContext ()
                let result =
                    ctx.Sample (SamplingRequest.simple "test" 10)
                    |> Async.AwaitTask |> Async.RunSynchronously
                Expect.equal result (Error SamplingNotSupported) "no-op"

            testCase "mockContext creates context with mock sample" <| fun _ ->
                let ctx = SamplingTool.mockContext "hello"
                let result =
                    ctx.Sample (SamplingRequest.simple "test" 10)
                    |> Async.AwaitTask |> Async.RunSynchronously
                Expect.isOk result "mock succeeds"
        ]

        testList "SamplingTool.define" [
            testCase "creates a tool that uses sampling in handler" <| fun _ ->
                let td =
                    SamplingTool.define<SummarizeArgs> "summarize" "Summarizes text via LLM"
                        (fun ctx args -> task {
                            let req = SamplingRequest.simple $"Summarize: {args.text}" 200
                            let! samplingResult = ctx.Sample req
                            match samplingResult with
                            | Ok r ->
                                match r.Message.Content with
                                | Text t -> return Ok [ Content.text t ]
                                | _ -> return Ok [ Content.text "no text" ]
                            | Error SamplingNotSupported ->
                                return Ok [ Content.text $"Fallback: {args.text.[..20]}..." ]
                            | Error e ->
                                return Error (TransportError $"Sampling failed: %A{e}")
                        })
                    |> Result.defaultWith (fun e -> failtest $"%A{e}")

                Expect.equal (ToolName.value td.Name) "summarize" "name"
                Expect.isSome td.InputSchema "has schema"

                // Handler uses noOp context → falls back
                let args = Map.ofList [
                    "text", JsonDocument.Parse("\"This is a long text that needs summarizing\"").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.stringContains t "Fallback" "uses fallback"
                | other -> failtest $"unexpected: %A{other}"

            testCase "returns error for empty name" <| fun _ ->
                let result =
                    SamplingTool.define<SummarizeArgs> "" "desc"
                        (fun _ _ -> Task.FromResult(Ok []))
                Expect.isError result "empty name"

            testCase "schema marks optional fields correctly" <| fun _ ->
                let td =
                    SamplingTool.define<SummarizeArgs> "s" "d"
                        (fun _ _ -> Task.FromResult(Ok []))
                    |> Result.defaultWith (fun e -> failtest $"%A{e}")
                let schema = td.InputSchema.Value
                let required =
                    match schema.TryGetProperty("required") with
                    | true, arr -> arr.EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
                    | _ -> Set.empty
                Expect.isTrue (required.Contains "text") "text required"
                Expect.isFalse (required.Contains "maxLength") "maxLength optional"
        ]
    ]
