#nowarn "44"

module FsMcp.Server.Tests.NotificationsTests

open System.Threading.Tasks
open Expecto
open FsMcp.Core
open FsMcp.Server
open FsMcp.Server.Notifications

type ProcessArgs = { count: int }

[<Tests>]
let notificationTests =
    testList "Notifications" [
        testCase "no-op context completes notification operations" <| fun _ ->
            let progress = { Progress = 0.5; Message = Some "halfway" }
            let log = { Level = Info; Message = "working"; Logger = None }
            HandlerContext.noOp.ReportProgress progress
            |> fun pending -> pending.GetAwaiter().GetResult()
            HandlerContext.noOp.Log log
            |> fun pending -> pending.GetAwaiter().GetResult()

        testCase "ContextualTool.define fails closed without request-scoped transport wiring" <| fun _ ->
            Expect.throwsT<FsMcpConfigException>
                (fun () ->
                    ContextualTool.define<ProcessArgs> "process" "Processes"
                        (fun _ _ -> Task.FromResult(Ok [ Content.text "unused" ]))
                    |> ignore)
                "a no-op notification context must not be presented as transport-backed"

        testCase "invokeWithContext supports explicitly constructed test handles" <| fun _ ->
            let definition =
                Tool.define "manual" "Manual test handle" (fun _ _ ->
                    Task.FromResult(Ok [ Content.text "definition" ]))
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
            let handle = {
                Definition = definition
                InvokeWithContext = fun context _ -> task {
                    do! context.Log { Level = Debug; Message = "manual"; Logger = None }
                    return Ok [ Content.text "manual" ]
                }
            }
            let result =
                ContextualTool.invokeWithContext HandlerContext.noOp handle Map.empty
                |> fun pending -> pending.GetAwaiter().GetResult()
            match result with
            | Ok [ Text "manual" ] -> ()
            | other -> failtest $"unexpected result: %A{other}"
    ]
