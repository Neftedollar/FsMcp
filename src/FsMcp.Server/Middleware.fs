namespace FsMcp.Server

open System.Threading.Tasks

/// Functions for composing middleware.
module Middleware =

    /// Compose two middleware functions. The first runs before the second.
    let compose (mw1: McpMiddleware) (mw2: McpMiddleware) : McpMiddleware =
        fun ctx next ->
            mw1 ctx (fun ctx' -> mw2 ctx' next)

    /// Compose a list of middleware into a single pipeline.
    /// Empty list returns a pass-through that calls next directly.
    let pipeline (middlewares: McpMiddleware list) : McpMiddleware =
        match middlewares with
        | [] -> fun ctx next -> next ctx
        | _ ->
            middlewares
            |> List.reduce compose
