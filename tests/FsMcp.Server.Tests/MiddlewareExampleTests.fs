module FsMcp.Server.Tests.MiddlewareExampleTests

open Expecto
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Server

/// Example: Logging middleware that records all requests.
let loggingMiddleware (log: ResizeArray<string>) : McpMiddleware =
    fun ctx next -> task {
        log.Add $"[LOG] {ctx.Method} started"
        let! response = next ctx
        let status =
            match response with
            | Success _ -> "OK"
            | McpResponseError _ -> "ERROR"
        log.Add $"[LOG] {ctx.Method} completed: {status}"
        return response
    }

/// Example: Auth middleware that rejects unauthorized requests.
let authMiddleware (allowedMethods: Set<string>) : McpMiddleware =
    fun ctx next ->
        if allowedMethods.Contains ctx.Method then
            next ctx
        else
            Task.FromResult(McpResponseError (TransportError $"Unauthorized: {ctx.Method}"))

/// Example: Timing middleware that measures handler duration.
let timingMiddleware (timings: ResizeArray<string * int64>) : McpMiddleware =
    fun ctx next -> task {
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let! response = next ctx
        sw.Stop()
        timings.Add (ctx.Method, sw.ElapsedMilliseconds)
        return response
    }

let okHandler (ctx: McpContext) : Task<McpResponse> =
    Task.FromResult(Success (System.Text.Json.JsonDocument.Parse("{}").RootElement))

[<Tests>]
let middlewareExamples =
    testList "Middleware examples" [
        testCase "logging middleware records request lifecycle" <| fun _ ->
            let log = ResizeArray<string>()
            let mw = loggingMiddleware log
            let ctx = { Method = "tools/call"; Params = None; CancellationToken = CancellationToken.None }
            mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal (Seq.toList log) [
                "[LOG] tools/call started"
                "[LOG] tools/call completed: OK"
            ] "log entries"

        testCase "auth middleware allows permitted methods" <| fun _ ->
            let mw = authMiddleware (Set.ofList ["tools/list"; "tools/call"])
            let ctx = { Method = "tools/list"; Params = None; CancellationToken = CancellationToken.None }
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Success _ -> ()
            | other -> failtest $"expected Success, got %A{other}"

        testCase "auth middleware rejects forbidden methods" <| fun _ ->
            let mw = authMiddleware (Set.ofList ["tools/list"])
            let ctx = { Method = "admin/shutdown"; Params = None; CancellationToken = CancellationToken.None }
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | McpResponseError (TransportError msg) ->
                Expect.stringContains msg "Unauthorized" "error message"
            | other -> failtest $"expected error, got %A{other}"

        testCase "composed logging + auth pipeline works end-to-end" <| fun _ ->
            let log = ResizeArray<string>()
            let pipeline = Middleware.pipeline [
                loggingMiddleware log
                authMiddleware (Set.ofList ["tools/call"])
            ]
            // Allowed request
            let ctx1 = { Method = "tools/call"; Params = None; CancellationToken = CancellationToken.None }
            pipeline ctx1 okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal log.Count 2 "two log entries for allowed"
            log.Clear()
            // Blocked request
            let ctx2 = { Method = "admin/delete"; Params = None; CancellationToken = CancellationToken.None }
            let result = pipeline ctx2 okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | McpResponseError _ -> ()
            | _ -> failtest "should be rejected"
            Expect.equal log.Count 2 "two log entries for blocked too"

        testCase "timing middleware captures duration" <| fun _ ->
            let timings = ResizeArray<string * int64>()
            let mw = timingMiddleware timings
            let ctx = { Method = "tools/call"; Params = None; CancellationToken = CancellationToken.None }
            mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal timings.Count 1 "one timing"
            let (method', _ms) = timings.[0]
            Expect.equal method' "tools/call" "method captured"
    ]
