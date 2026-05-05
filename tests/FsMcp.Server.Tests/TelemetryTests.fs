module FsMcp.Server.Tests.TelemetryTests

open Expecto
open System
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Server

let mkContext method' =
    { Method = method'; Params = None; CancellationToken = CancellationToken.None }

let okHandler (_ctx: McpContext) : Task<McpResponse> =
    Task.FromResult(Success (JsonDocument.Parse("{}").RootElement))

let errorHandler (_ctx: McpContext) : Task<McpResponse> =
    Task.FromResult(McpResponseError (TransportError "test error"))

let throwHandler (_ctx: McpContext) : Task<McpResponse> =
    raise (InvalidOperationException("boom"))

/// Set up an ActivityListener that captures activities from FsMcp.Server.
/// Returns activities filtered by the given operation name.
let withActivityListener (operationName: string) (f: ResizeArray<Activity> -> unit) =
    let captured = ResizeArray<Activity>()
    use listener = new ActivityListener()
    listener.ShouldListenTo <- fun source -> source.Name = "FsMcp.Server"
    listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    listener.ActivityStopped <- fun a ->
        if a.OperationName = operationName then
            captured.Add(a)
    ActivitySource.AddActivityListener(listener)
    f captured

[<Tests>]
let telemetryTracingTests =
    testList "Telemetry.tracing" [
        testCase "creates Activity with mcp.method tag" <| fun _ ->
            withActivityListener "test/method-tag" (fun captured ->
                let mw = Telemetry.tracing ()
                let ctx = mkContext "test/method-tag"
                mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                Expect.isGreaterThan captured.Count 0 "should capture at least one activity"
                let activity = captured[0]
                let methodTag = activity.GetTagItem("mcp.method") :?> string
                Expect.equal methodTag "test/method-tag" "mcp.method tag"
            )

        testCase "sets mcp.status=ok on success" <| fun _ ->
            withActivityListener "test/status-ok" (fun captured ->
                let mw = Telemetry.tracing ()
                let ctx = mkContext "test/status-ok"
                let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Success _ -> ()
                | McpResponseError e -> failtest $"expected Success, got error: %A{e}"
                Expect.isGreaterThan captured.Count 0 "should capture activity"
                let activity = captured[0]
                let status = activity.GetTagItem("mcp.status") :?> string
                Expect.equal status "ok" "mcp.status should be ok"
            )

        testCase "sets mcp.status=error on McpResponseError" <| fun _ ->
            withActivityListener "test/status-error" (fun captured ->
                let mw = Telemetry.tracing ()
                let ctx = mkContext "test/status-error"
                let result = mw ctx errorHandler |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | McpResponseError _ -> ()
                | Success _ -> failtest "expected error"
                Expect.isGreaterThan captured.Count 0 "should capture activity"
                let activity = captured[0]
                let status = activity.GetTagItem("mcp.status") :?> string
                Expect.equal status "error" "mcp.status should be error"
                Expect.equal activity.Status ActivityStatusCode.Error "activity status code"
            )

        testCase "sets exception info on throw" <| fun _ ->
            withActivityListener "test/status-exception" (fun captured ->
                let mw = Telemetry.tracing ()
                let ctx = mkContext "test/status-exception"
                let result = mw ctx throwHandler |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | McpResponseError (HandlerException ex) ->
                    Expect.equal ex.Message "boom" "exception message"
                | other -> failtest $"expected HandlerException, got %A{other}"
                Expect.isGreaterThan captured.Count 0 "should capture activity"
                let activity = captured[0]
                let status = activity.GetTagItem("mcp.status") :?> string
                Expect.equal status "exception" "mcp.status should be exception"
                let errorMsg = activity.GetTagItem("mcp.error") :?> string
                Expect.equal errorMsg "boom" "mcp.error tag"
                Expect.equal activity.Status ActivityStatusCode.Error "activity status code"
            )

        testCase "sets mcp.duration_ms tag" <| fun _ ->
            withActivityListener "test/duration" (fun captured ->
                let mw = Telemetry.tracing ()
                let ctx = mkContext "test/duration"
                mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                Expect.isGreaterThan captured.Count 0 "should capture activity"
                let activity = captured[0]
                let duration = activity.GetTagItem("mcp.duration_ms")
                Expect.isNotNull duration "mcp.duration_ms should be set"
            )
    ]

[<Tests>]
let telemetryMetricsTests =
    testList "Telemetry.MetricsCollector" [
        testCase "counts requests per method" <| fun _ ->
            let collector = Telemetry.MetricsCollector()
            let mw = collector.Middleware

            mw (mkContext "tools/call") okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            mw (mkContext "tools/call") okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            mw (mkContext "tools/list") okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore

            let counts = collector.RequestCounts
            Expect.equal (Map.find "tools/call" counts) 2 "tools/call count"
            Expect.equal (Map.find "tools/list" counts) 1 "tools/list count"

        testCase "tracks average durations" <| fun _ ->
            let collector = Telemetry.MetricsCollector()
            let mw = collector.Middleware

            mw (mkContext "tools/call") okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            mw (mkContext "tools/call") okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore

            let avgs = collector.AverageDurations
            Expect.isTrue (Map.containsKey "tools/call" avgs) "should track tools/call"
            Expect.isGreaterThanOrEqual (Map.find "tools/call" avgs) 0.0 "average should be non-negative"

        testCase "Middleware composes with other middleware" <| fun _ ->
            let log = ResizeArray<string>()
            let logMw : McpMiddleware = fun ctx next -> task {
                log.Add "before"
                let! r = next ctx
                log.Add "after"
                return r
            }
            let collector = Telemetry.MetricsCollector()
            let pipeline = Middleware.pipeline [ logMw; collector.Middleware ]
            let ctx = mkContext "tools/call"
            pipeline ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore

            Expect.equal (Seq.toList log) ["before"; "after"] "log middleware ran"
            let counts = collector.RequestCounts
            Expect.equal (Map.find "tools/call" counts) 1 "collector counted request"
    ]

[<Tests>]
let telemetryAllWithCollectorTests =
    testList "Telemetry.allWithCollector" [
        testCase "creates combined tracing+metering middleware" <| fun _ ->
            withActivityListener "test/all-combined" (fun captured ->
                let collector, mw = Telemetry.allWithCollector ()
                let ctx = mkContext "test/all-combined"
                let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Success _ -> ()
                | McpResponseError e -> failtest $"expected Success, got error: %A{e}"
                // Tracing should have created an activity
                Expect.isGreaterThan captured.Count 0 "should capture activity from tracing"
                let activity = captured[0]
                let status = activity.GetTagItem("mcp.status") :?> string
                Expect.equal status "ok" "tracing should set ok status"
                // Collector should have observed the request (A1 test requirement)
                let counts = collector.RequestCounts
                Expect.isTrue (Map.containsKey "test/all-combined" counts) "collector has method"
                let avgs = collector.AverageDurations
                Expect.isTrue (Map.containsKey "test/all-combined" avgs) "AverageDurations is non-empty"
            )
    ]
