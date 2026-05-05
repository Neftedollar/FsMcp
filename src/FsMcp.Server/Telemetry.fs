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

    type internal RingBuffer(capacity: int) =
        let buffer = Array.zeroCreate<int64> capacity
        let mutable head = 0
        let mutable count = 0
        member _.Add(value: int64) =
            buffer[head] <- value
            head <- (head + 1) % capacity
            if count < capacity then count <- count + 1
        member _.Average() : float =
            if count = 0 then 0.0
            else
                let mutable sum = 0L
                for i = 0 to count - 1 do
                    sum <- sum + buffer[i]
                float sum / float count
        member _.Count = count

    /// Metrics collector that tracks request counts and durations.
    /// Keeps only the last 1000 durations per method to prevent memory leaks.
    type MetricsCollector(?maxDurationsPerMethod: int) =
        let maxDurations = defaultArg maxDurationsPerMethod 1000
        let requestCounts = System.Collections.Concurrent.ConcurrentDictionary<string, int ref>()
        let durations = System.Collections.Concurrent.ConcurrentDictionary<string, RingBuffer>()

        /// Get request count per method.
        member _.RequestCounts =
            requestCounts |> Seq.map (fun kv -> kv.Key, kv.Value.Value) |> Map.ofSeq

        /// Get average duration per method in ms.
        /// Best-effort snapshot. Concurrent recording may yield mildly stale arithmetic;
        /// do not rely on exact values for billing or SLO calculations.
        member _.AverageDurations =
            durations
            |> Seq.map (fun kv -> kv.Key, lock kv.Value (fun () -> kv.Value.Average()))
            |> Map.ofSeq

        /// Create a middleware that records to this collector.
        member _.Middleware : McpMiddleware =
            fun ctx next -> task {
                let counter = requestCounts.GetOrAdd(ctx.Method, fun _ -> ref 0)
                System.Threading.Interlocked.Increment(counter) |> ignore
                let sw = Stopwatch.StartNew()
                let! response = next ctx
                sw.Stop()
                let durs = durations.GetOrAdd(ctx.Method, fun _ -> RingBuffer(maxDurations))
                lock durs (fun () -> durs.Add(sw.ElapsedMilliseconds))
                return response
            }

    /// Combined tracing + metering middleware, also returning the collector for inspection.
    let allWithCollector () : MetricsCollector * McpMiddleware =
        let collector = MetricsCollector()
        collector, Middleware.compose (tracing()) collector.Middleware

    /// Combined tracing + metering middleware. Source-compatible 1.0.x shim;
    /// prefer allWithCollector for new code, which also returns the collector
    /// so RequestCounts/AverageDurations can be inspected.
    [<System.Obsolete("Use allWithCollector if you need access to the MetricsCollector for inspection.")>]
    let all () : McpMiddleware =
        allWithCollector () |> snd
