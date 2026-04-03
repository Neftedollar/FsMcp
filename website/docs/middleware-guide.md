---
sidebar_position: 4
description: "Compose logging, auth, validation, and telemetry middleware for FsMcp servers using the McpMiddleware pipeline."
---


# Middleware Guide

## What is `McpMiddleware`?

```fsharp
type McpMiddleware =
    McpContext -> (McpContext -> Task<McpResponse>) -> Task<McpResponse>
```

A middleware is a function that receives:

1. **`McpContext`** -- the current request context with `Method` (e.g., `"tools/call"`), optional `Params` (a `JsonElement`), and a `CancellationToken`.
2. **`next`** -- a function to call the next handler (or middleware) in the chain.

It returns a `Task<McpResponse>` where `McpResponse` is either `Success of JsonElement` or `McpResponseError of McpError`.

The middleware can inspect or modify the context before calling `next`, inspect or modify the response after, or short-circuit by returning a response without calling `next`.

## Writing a logging middleware

```fsharp
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Server

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
```

This middleware wraps every request with before/after logging. It always calls `next` -- it never short-circuits.

## Writing an auth middleware

```fsharp
let authMiddleware (allowedMethods: Set<string>) : McpMiddleware =
    fun ctx next ->
        if allowedMethods.Contains ctx.Method then
            next ctx
        else
            Task.FromResult(McpResponseError (TransportError $"Unauthorized: {ctx.Method}"))
```

This middleware short-circuits: if the method is not in the allowed set, it returns an error immediately without calling `next`.

## `Middleware.compose` and `Middleware.pipeline`

### Compose two middleware

`Middleware.compose` chains two middleware so the first runs before the second:

```fsharp
let combined = Middleware.compose (loggingMiddleware log) (authMiddleware allowed)
```

Execution order: logging wraps auth wraps the handler.

### Compose a list into a pipeline

`Middleware.pipeline` reduces a list of middleware into a single middleware. An empty list returns a pass-through:

```fsharp
let pipeline = Middleware.pipeline [
    loggingMiddleware log
    authMiddleware (Set.ofList ["tools/call"; "tools/list"])
    timingMiddleware timings
]
```

Execution flows left-to-right: logging -> auth -> timing -> handler -> timing -> auth -> logging.

## `ValidationMiddleware.create`

The built-in validation middleware checks that tool call arguments include all required fields from the tool's JSON Schema before the handler runs. It only applies to `tools/call` requests against tools that have an `InputSchema`:

```fsharp
open FsMcp.Server

let server = mcpServer {
    name "ValidatedServer"
    version "1.0.0"

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args -> task {
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    useStdio
}

// Create validation middleware from the config
let validationMw = ValidationMiddleware.create server
```

If a required field is missing, the middleware returns `McpResponseError (TransportError "Validation failed: Missing required fields: a, b")` without ever calling the handler.

## `Telemetry.tracing()` and `MetricsCollector`

### Tracing middleware

`Telemetry.tracing()` creates a middleware that emits a `System.Diagnostics.Activity` (span) for each request. Compatible with OpenTelemetry, Application Insights, and any `ActivityListener`:

```fsharp
open FsMcp.Server

let tracingMw = Telemetry.tracing ()
```

Tags emitted: `mcp.method`, `mcp.status` (`"ok"` or `"error"`), `mcp.duration_ms`, and `mcp.error` (on exceptions).

### MetricsCollector

`MetricsCollector` tracks request counts and average durations per method:

```fsharp
let collector = Telemetry.MetricsCollector()

// Use collector.Middleware in your pipeline
let metricsMw = collector.Middleware

// Later, query metrics:
let counts = collector.RequestCounts      // Map<string, int>
let avgDurations = collector.AverageDurations  // Map<string, float>
```

The collector keeps only the last 1000 durations per method to prevent memory leaks. You can customize this:

```fsharp
let collector = Telemetry.MetricsCollector(maxDurationsPerMethod = 500)
```

### Combined tracing + metrics

```fsharp
let allTelemetry = Telemetry.all ()
```

This composes `tracing()` and a `MetricsCollector` into a single middleware.

## Composing everything in `mcpServer { }`

```fsharp
open FsMcp.Core
open FsMcp.Server

type CalcArgs = { a: float; b: float }

let log = ResizeArray<string>()

let server = mcpServer {
    name "FullServer"
    version "1.0.0"

    tool (TypedTool.define<CalcArgs> "add" "Add two numbers" (fun args -> task {
        return Ok [ Content.text $"{args.a + args.b}" ]
    }) |> unwrapResult)

    middleware (Telemetry.tracing ())

    middleware (fun ctx next -> task {
        log.Add $"[LOG] {ctx.Method}"
        return! next ctx
    })

    useStdio
}

// Add validation after building (needs the config to inspect schemas):
let validatedServer =
    { server with
        Middleware = server.Middleware @ [ ValidationMiddleware.create server ] }
```

Middleware runs in the order they are added. Each `middleware` call appends to the list. At runtime the pipeline is: first middleware wraps second wraps third wraps the actual handler.
