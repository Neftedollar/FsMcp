module FsMcp.Server.Tests.NotificationsTests

open Expecto
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open FsMcp.Server.Notifications

// ───────── Helpers ─────────

let unwrap r = match r with Result.Ok v -> v | Result.Error e -> failtest $"%A{e}"

/// Create a HandlerContext that captures progress and log calls.
let captureContext () =
    let progressCalls = ResizeArray<ProgressUpdate>()
    let logCalls = ResizeArray<LogEntry>()
    let ctx : HandlerContext = {
        ReportProgress = fun p -> progressCalls.Add(p); Task.FromResult(())
        Log = fun l -> logCalls.Add(l); Task.FromResult(())
        CancellationToken = CancellationToken.None
    }
    ctx, progressCalls, logCalls

// ───────── Typed args ─────────

type ProcessArgs = { input: string }

// ───────── Tests ─────────

[<Tests>]
let notificationsTests =
    testList "Notifications" [
        testList "HandlerContext" [
            testCase "ReportProgress captures progress updates" <| fun _ ->
                let ctx, progressCalls, _ = captureContext ()
                ctx.ReportProgress { Progress = 0.5; Message = Some "halfway" }
                |> Async.AwaitTask |> Async.RunSynchronously
                ctx.ReportProgress { Progress = 1.0; Message = None }
                |> Async.AwaitTask |> Async.RunSynchronously
                Expect.equal progressCalls.Count 2 "two progress calls"
                Expect.equal progressCalls.[0].Progress 0.5 "first progress"
                Expect.equal progressCalls.[0].Message (Some "halfway") "first message"
                Expect.equal progressCalls.[1].Progress 1.0 "second progress"
                Expect.equal progressCalls.[1].Message None "second message"

            testCase "Log captures log entries" <| fun _ ->
                let ctx, _, logCalls = captureContext ()
                ctx.Log { Level = McpLogLevel.Info; Message = "starting"; Logger = Some "test" }
                |> Async.AwaitTask |> Async.RunSynchronously
                ctx.Log { Level = McpLogLevel.Error; Message = "failed"; Logger = None }
                |> Async.AwaitTask |> Async.RunSynchronously
                Expect.equal logCalls.Count 2 "two log calls"
                Expect.equal logCalls.[0].Level McpLogLevel.Info "first level"
                Expect.equal logCalls.[0].Message "starting" "first message"
                Expect.equal logCalls.[0].Logger (Some "test") "first logger"
                Expect.equal logCalls.[1].Level McpLogLevel.Error "second level"
                Expect.equal logCalls.[1].Message "failed" "second message"
                Expect.equal logCalls.[1].Logger None "second logger"

            testCase "no-op context does not crash" <| fun _ ->
                let noOpCtx = HandlerContext.noOp
                noOpCtx.ReportProgress { Progress = 0.5; Message = None }
                |> Async.AwaitTask |> Async.RunSynchronously
                noOpCtx.Log { Level = McpLogLevel.Debug; Message = "test"; Logger = None }
                |> Async.AwaitTask |> Async.RunSynchronously
                // Just ensure no exception was thrown
                Expect.isTrue true "no-op completed without error"
        ]

        testList "ContextualTool.define" [
            testCase "creates valid tool with context" <| fun _ ->
                let td =
                    ContextualTool.define<ProcessArgs> "process" "Processes input"
                        (fun ctx args -> task {
                            do! ctx.ReportProgress { Progress = 0.0; Message = Some "starting" }
                            do! ctx.Log { Level = McpLogLevel.Info; Message = $"Processing {args.input}"; Logger = None }
                            do! ctx.ReportProgress { Progress = 1.0; Message = Some "done" }
                            return Result.Ok [ Content.text $"processed: {args.input}" ]
                        })
                    |> unwrap
                Expect.equal (ToolName.value td.Name) "process" "name"
                Expect.isSome td.InputSchema "has schema"

            testCase "handler invokes with no-op context and returns result" <| fun _ ->
                let td =
                    ContextualTool.define<ProcessArgs> "process" "Processes"
                        (fun ctx args -> task {
                            do! ctx.ReportProgress { Progress = 1.0; Message = None }
                            return Result.Ok [ Content.text $"done: {args.input}" ]
                        })
                    |> unwrap
                let args = Map.ofList [
                    "input", JsonDocument.Parse("\"hello\"").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Result.Ok [ Text t ] -> Expect.equal t "done: hello" "result text"
                | other -> failtest $"unexpected: %A{other}"

            testCase "handler captures progress and log when wired up" <| fun _ ->
                let progressCalls = ResizeArray<ProgressUpdate>()
                let logCalls = ResizeArray<LogEntry>()
                let td =
                    ContextualTool.define<ProcessArgs> "process" "Processes"
                        (fun ctx args -> task {
                            do! ctx.ReportProgress { Progress = 0.5; Message = Some "half" }
                            do! ctx.Log { Level = McpLogLevel.Info; Message = "working"; Logger = Some "test" }
                            return Result.Ok [ Content.text "ok" ]
                        })
                    |> unwrap
                // Rewire the handler with a capturing context
                let ctx : HandlerContext = {
                    ReportProgress = fun p -> progressCalls.Add(p); Task.FromResult(())
                    Log = fun l -> logCalls.Add(l); Task.FromResult(())
                    CancellationToken = CancellationToken.None
                }
                let args = Map.ofList [
                    "input", JsonDocument.Parse("\"test\"").RootElement
                ]
                // Use ContextualTool.invokeWithContext to run with custom context
                let result =
                    ContextualTool.invokeWithContext ctx td args
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Result.Ok [ Text t ] ->
                    Expect.equal t "ok" "result text"
                    Expect.equal progressCalls.Count 1 "one progress"
                    Expect.equal progressCalls.[0].Progress 0.5 "progress value"
                    Expect.equal logCalls.Count 1 "one log"
                    Expect.equal logCalls.[0].Message "working" "log message"
                | other -> failtest $"unexpected: %A{other}"

            testCase "returns error for empty tool name" <| fun _ ->
                let result =
                    ContextualTool.define<ProcessArgs> "" "d"
                        (fun _ _ -> Task.FromResult(Result.Ok []))
                Expect.isError result "empty name"

            testCase "handler returns error for invalid args" <| fun _ ->
                let td =
                    ContextualTool.define<{| count: int |}> "process" "Processes"
                        (fun _ _ -> Task.FromResult(Result.Ok [ Content.text "ok" ]))
                    |> unwrap
                let args = Map.ofList [
                    "count", JsonDocument.Parse("\"not-a-number\"").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                Expect.isError result "invalid args"
        ]

        testList "mcpServer CE integration" [
            testCase "contextual tool works in mcpServer CE" <| fun _ ->
                let config = mcpServer {
                    name "NotifServer"
                    version "1.0.0"
                    tool (ContextualTool.define<ProcessArgs> "process" "Processes"
                        (fun ctx args -> task {
                            do! ctx.ReportProgress { Progress = 1.0; Message = None }
                            return Result.Ok [ Content.text $"done: {args.input}" ]
                        }) |> unwrap)
                    useStdio
                }
                Expect.equal (List.length config.Tools) 1 "one tool"
                Expect.isSome config.Tools.[0].InputSchema "has schema"
        ]

        testList "McpLogLevel" [
            testCase "all log levels exist" <| fun _ ->
                let levels = [ McpLogLevel.Debug; McpLogLevel.Info; McpLogLevel.Warning; McpLogLevel.Error ]
                Expect.equal levels.Length 4 "four log levels"
        ]
    ]
