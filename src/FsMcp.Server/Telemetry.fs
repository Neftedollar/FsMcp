namespace FsMcp.Server

open System
open System.Diagnostics
open System.Threading.Tasks
open FsMcp.Core

/// Built-in telemetry middleware using System.Diagnostics.Activity.
/// Compatible with OpenTelemetry, Application Insights, and any ActivityListener.
module Telemetry =

    /// ActivitySource for FsMcp server operations.
    let activitySource = new ActivitySource("FsMcp.Server", "1.0.0")

    /// Creates a middleware that traces each MCP request as an Activity (span).
    /// Tags: mcp.method, mcp.status, mcp.error (if error), mcp.duration_ms
    let tracing () : McpMiddleware =
        fun ctx next -> task {
            use activity = activitySource.StartActivity(ctx.Method, ActivityKind.Server)
            if not (isNull activity) then
                activity.SetTag("mcp.method", ctx.Method) |> ignore
            let sw = Stopwatch.StartNew()
            try
                let! response = next ctx
                sw.Stop()
                if not (isNull activity) then
                    activity.SetTag("mcp.duration_ms", sw.ElapsedMilliseconds) |> ignore
                    match response with
                    | Success _ -> activity.SetTag("mcp.status", "ok") |> ignore
                    | McpResponseError _ ->
                        activity.SetTag("mcp.status", "error") |> ignore
                        activity.SetStatus(ActivityStatusCode.Error) |> ignore
                return response
            with ex ->
                sw.Stop()
                if not (isNull activity) then
                    activity.SetTag("mcp.status", "exception") |> ignore
                    activity.SetTag("mcp.error", ex.Message) |> ignore
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message) |> ignore
                return McpResponseError (HandlerException ex)
        }

    /// Metrics collector that tracks request counts and durations.
    type MetricsCollector() =
        let requestCounts = System.Collections.Concurrent.ConcurrentDictionary<string, int ref>()
        let durations = System.Collections.Concurrent.ConcurrentDictionary<string, ResizeArray<int64>>()

        /// Get request count per method.
        member _.RequestCounts =
            requestCounts |> Seq.map (fun kv -> kv.Key, kv.Value.Value) |> Map.ofSeq

        /// Get average duration per method in ms.
        member _.AverageDurations =
            durations
            |> Seq.map (fun kv ->
                let avg = if kv.Value.Count > 0 then kv.Value |> Seq.averageBy float else 0.0
                kv.Key, avg)
            |> Map.ofSeq

        /// Create a middleware that records to this collector.
        member _.Middleware : McpMiddleware =
            fun ctx next -> task {
                let counter = requestCounts.GetOrAdd(ctx.Method, fun _ -> ref 0)
                System.Threading.Interlocked.Increment(counter) |> ignore
                let sw = Stopwatch.StartNew()
                let! response = next ctx
                sw.Stop()
                let durs = durations.GetOrAdd(ctx.Method, fun _ -> ResizeArray<int64>())
                lock durs (fun () -> durs.Add(sw.ElapsedMilliseconds))
                return response
            }

    /// Combined tracing + metering middleware.
    let all () : McpMiddleware =
        let collector = MetricsCollector()
        Middleware.compose (tracing()) collector.Middleware
