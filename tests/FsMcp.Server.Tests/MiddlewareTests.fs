module FsMcp.Server.Tests.MiddlewareTests

open Expecto
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Server

let mkContext method' =
    { Method = method'; Params = None; CancellationToken = CancellationToken.None }

let okHandler (ctx: McpContext) : Task<McpResponse> =
    Task.FromResult(Success (System.Text.Json.JsonDocument.Parse("{}").RootElement))

[<Tests>]
let middlewareTests =
    testList "Middleware" [
        testCase "empty pipeline passes through to handler" <| fun _ ->
            let mw = Middleware.pipeline []
            let ctx = mkContext "test"
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Success _ -> ()
            | McpResponseError e -> failtest $"expected Success, got error: %A{e}"

        testCase "single middleware executes before handler" <| fun _ ->
            let log = ResizeArray<string>()
            let mw : McpMiddleware =
                fun ctx next -> task {
                    log.Add "before"
                    let! result = next ctx
                    log.Add "after"
                    return result
                }
            let ctx = mkContext "test"
            mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal (Seq.toList log) ["before"; "after"] "execution order"

        testCase "compose executes first middleware before second" <| fun _ ->
            let log = ResizeArray<string>()
            let mw1 : McpMiddleware =
                fun ctx next -> task {
                    log.Add "mw1-before"
                    let! result = next ctx
                    log.Add "mw1-after"
                    return result
                }
            let mw2 : McpMiddleware =
                fun ctx next -> task {
                    log.Add "mw2-before"
                    let! result = next ctx
                    log.Add "mw2-after"
                    return result
                }
            let composed = Middleware.compose mw1 mw2
            let ctx = mkContext "test"
            composed ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal (Seq.toList log) ["mw1-before"; "mw2-before"; "mw2-after"; "mw1-after"] "onion order"

        testCase "pipeline executes middleware in declared order" <| fun _ ->
            let log = ResizeArray<string>()
            let mkLogMw name : McpMiddleware =
                fun ctx next -> task {
                    log.Add $"{name}-before"
                    let! result = next ctx
                    log.Add $"{name}-after"
                    return result
                }
            let mw = Middleware.pipeline [ mkLogMw "A"; mkLogMw "B"; mkLogMw "C" ]
            let ctx = mkContext "test"
            mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal (Seq.toList log)
                ["A-before"; "B-before"; "C-before"; "C-after"; "B-after"; "A-after"]
                "onion order with 3 middleware"

        testCase "middleware can reject request without calling next" <| fun _ ->
            let rejectMw : McpMiddleware =
                fun _ctx _next ->
                    Task.FromResult(McpResponseError (TransportError "rejected"))
            let ctx = mkContext "blocked"
            let result = rejectMw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | McpResponseError (TransportError msg) -> Expect.equal msg "rejected" "error message"
            | other -> failtest $"expected error, got %A{other}"

        testCase "rejecting middleware prevents handler execution" <| fun _ ->
            let handlerCalled = ref false
            let rejectMw : McpMiddleware =
                fun _ctx _next ->
                    Task.FromResult(McpResponseError (TransportError "nope"))
            let trackingHandler (ctx: McpContext) : Task<McpResponse> =
                handlerCalled.Value <- true
                okHandler ctx
            let ctx = mkContext "test"
            rejectMw ctx trackingHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.isFalse handlerCalled.Value "handler should not be called"

        testCase "middleware can modify context before passing to next" <| fun _ ->
            let modifyMw : McpMiddleware =
                fun ctx next ->
                    let modified = { ctx with Method = ctx.Method + "-modified" }
                    next modified
            let capturedMethod = ref ""
            let capturingHandler (ctx: McpContext) : Task<McpResponse> =
                capturedMethod.Value <- ctx.Method
                okHandler ctx
            let ctx = mkContext "original"
            modifyMw ctx capturingHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal capturedMethod.Value "original-modified" "modified method"

        testCase "compose with empty second acts as first only" <| fun _ ->
            let log = ResizeArray<string>()
            let mw1 : McpMiddleware =
                fun ctx next -> task {
                    log.Add "mw1"
                    return! next ctx
                }
            let passthrough : McpMiddleware = fun ctx next -> next ctx
            let composed = Middleware.compose mw1 passthrough
            composed (mkContext "test") okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal (Seq.toList log) ["mw1"] "only mw1 logged"
    ]
