#nowarn "44"

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

        testCase "SamplingTool.define fails closed instead of injecting a no-op client" <| fun _ ->
            Expect.throwsT<FsMcp.Server.FsMcpConfigException>
                (fun () ->
                    SamplingTool.define<SummarizeArgs> "summarize" "Summarizes text"
                        (fun _ _ -> Task.FromResult(Ok [ Content.text "unused" ]))
                    |> ignore)
                "sampling requires an actual request-scoped MCP server"
    ]
