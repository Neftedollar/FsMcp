module FsMcp.Server.Tests.NotificationsTests

open Expecto
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open FsMcp.Server.Notifications

let unwrap r = match r with Result.Ok v -> v | Result.Error e -> failtest $"%A{e}"

type ProcessArgs = { input: string }

[<Tests>]
let notificationsTests =
    testList "Notifications" [
        testList "HandlerContext" [
            testCase "ReportProgress captures progress updates" <| fun _ ->
                let calls = ResizeArray<ProgressUpdate>()
                let ctx : HandlerContext = {
                    ReportProgress = fun p -> calls.Add(p); Task.FromResult(())
                    Log = fun _ -> Task.FromResult(())
                    CancellationToken = CancellationToken.None
                }
                ctx.ReportProgress { Progress = 0.5; Message = Some "halfway" }
                |> Async.AwaitTask |> Async.RunSynchronously
                Expect.equal calls.Count 1 "one call"
                Expect.equal calls.[0].Progress 0.5 "value"

            testCase "Log captures log entries" <| fun _ ->
                let calls = ResizeArray<LogEntry>()
                let ctx : HandlerContext = {
                    ReportProgress = fun _ -> Task.FromResult(())
                    Log = fun l -> calls.Add(l); Task.FromResult(())
                    CancellationToken = CancellationToken.None
                }
                ctx.Log { Level = McpLogLevel.Info; Message = "test"; Logger = Some "src" }
                |> Async.AwaitTask |> Async.RunSynchronously
                Expect.equal calls.[0].Level McpLogLevel.Info "level"
                Expect.equal calls.[0].Message "test" "msg"

            testCase "no-op context does not crash" <| fun _ ->
                HandlerContext.noOp.ReportProgress { Progress = 1.0; Message = None }
                |> Async.AwaitTask |> Async.RunSynchronously
                HandlerContext.noOp.Log { Level = McpLogLevel.Debug; Message = "x"; Logger = None }
                |> Async.AwaitTask |> Async.RunSynchronously
        ]

        testList "ContextualTool.define" [
            testCase "creates valid tool with context" <| fun _ ->
                let handle =
                    ContextualTool.define<ProcessArgs> "process" "Processes"
                        (fun ctx args -> task {
                            do! ctx.ReportProgress { Progress = 1.0; Message = None }
                            return Result.Ok [ Content.text $"done: {args.input}" ]
                        })
                    |> unwrap
                Expect.equal (ToolName.value handle.Definition.Name) "process" "name"
                Expect.isSome handle.Definition.InputSchema "has schema"

            testCase "handler invokes with no-op context" <| fun _ ->
                let handle =
                    ContextualTool.define<ProcessArgs> "proc2" "Processes"
                        (fun _ctx args -> task {
                            return Result.Ok [ Content.text $"done: {args.input}" ]
                        })
                    |> unwrap
                let args = Map.ofList [ "input", JsonDocument.Parse("\"hello\"").RootElement ]
                let result = handle.Definition.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Result.Ok [ Text t ] -> Expect.equal t "done: hello" "result"
                | other -> failtest $"unexpected: %A{other}"

            testCase "invokeWithContext captures progress and log" <| fun _ ->
                let progressCalls = ResizeArray<ProgressUpdate>()
                let logCalls = ResizeArray<LogEntry>()
                let handle =
                    ContextualTool.define<ProcessArgs> "proc3" "Processes"
                        (fun ctx _args -> task {
                            do! ctx.ReportProgress { Progress = 0.5; Message = Some "half" }
                            do! ctx.Log { Level = McpLogLevel.Info; Message = "working"; Logger = Some "test" }
                            return Result.Ok [ Content.text "ok" ]
                        })
                    |> unwrap
                let ctx : HandlerContext = {
                    ReportProgress = fun p -> progressCalls.Add(p); Task.FromResult(())
                    Log = fun l -> logCalls.Add(l); Task.FromResult(())
                    CancellationToken = CancellationToken.None
                }
                let args = Map.ofList [ "input", JsonDocument.Parse("\"test\"").RootElement ]
                let result =
                    ContextualTool.invokeWithContext ctx handle args
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Result.Ok [ Text t ] ->
                    Expect.equal t "ok" "result"
                    Expect.equal progressCalls.Count 1 "progress"
                    Expect.equal progressCalls.[0].Progress 0.5 "value"
                    Expect.equal logCalls.Count 1 "log"
                    Expect.equal logCalls.[0].Message "working" "msg"
                | other -> failtest $"unexpected: %A{other}"

            testCase "returns error for empty tool name" <| fun _ ->
                let result =
                    ContextualTool.define<ProcessArgs> "" "d"
                        (fun _ _ -> Task.FromResult(Result.Ok []))
                Expect.isError result "empty name"

            testCase "handler returns error for invalid args" <| fun _ ->
                let handle =
                    ContextualTool.define<{| count: int |}> "proc4" "Processes"
                        (fun _ _ -> Task.FromResult(Result.Ok [ Content.text "ok" ]))
                    |> unwrap
                let args = Map.ofList [ "count", JsonDocument.Parse("\"not-a-number\"").RootElement ]
                let result = handle.Definition.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                Expect.isError result "invalid args"
        ]

        testList "mcpServer CE integration" [
            testCase "contextual tool works in mcpServer CE" <| fun _ ->
                let handle =
                    ContextualTool.define<ProcessArgs> "proc5" "Processes"
                        (fun ctx args -> task {
                            do! ctx.ReportProgress { Progress = 1.0; Message = None }
                            return Result.Ok [ Content.text $"done: {args.input}" ]
                        })
                    |> unwrap
                let config = mcpServer {
                    name "NotifServer"
                    version "1.0.0"
                    tool handle.Definition
                    useStdio
                }
                Expect.equal (List.length config.Tools) 1 "one tool"
        ]
    ]
