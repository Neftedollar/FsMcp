// FS0057: HttpServerTransportOptions.RunSessionHandler is experimental (SDK diagnostic MCPEXP002).
// Suppressed here because this is the sole call site in the codebase that sets RunSessionHandler.
// All other files in FsMcp.Server.Http are unaffected.
#nowarn "57"

namespace FsMcp.Server.Http

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Builder
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open ModelContextProtocol.Server
open ModelContextProtocol.Protocol

/// HTTP/SSE transport for FsMcp servers.
/// Requires the ModelContextProtocol.AspNetCore package.
/// Add this package only if you need HTTP transport;
/// stdio-only servers use FsMcp.Server directly.
module HttpServer =

    /// Convert F# ToolDefinition to SDK McpServerTool (reuses Server module's logic).
    let private registerAll (builder: IMcpServerBuilder) (config: ServerConfig) =
        // Reuse the same bridge logic as Server.run
        // We need access to the internal createSdkTool etc. from Transport.fs
        // Since Transport.fs's functions are in the Server module, and we reference FsMcp.Server,
        // we call Server.registerAll which we'll make internal+visible
        ()

    /// Run the MCP server over HTTP (Streamable HTTP + SSE).
    let run (config: ServerConfig) (endpoint: string option) (url: string) : Task<unit> =
        task {
            let builder = WebApplication.CreateBuilder()
            builder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

            let mcpBuilder = builder.Services.AddMcpServer()
            mcpBuilder.WithHttpTransport() |> ignore

            FsMcp.Server.Server.registerAllInternal mcpBuilder config |> ignore

            use app = builder.Build()
            let route = endpoint |> Option.defaultValue "/"
            app.MapMcp(route) |> ignore
            app.Urls.Add(url)
            do! app.RunAsync()
        }

    /// Run the MCP server over HTTP and expose the subscription registry.
    /// The onRegistry callback fires once, before the HTTP host starts, with the
    /// registry (or None when there are no resources).
    ///
    /// HTTP transport: when a client session ends, automatically calls
    /// ResourceSubscriptions.unsubscribeAllForSession to drop per-session state
    /// and McpServer references. The cleanup is wired via SDK RunSessionHandler
    /// (experimental — MCPEXP002 / F# FS0057, suppressed at file level because
    /// this is the sole call site in the codebase).
    ///
    /// Stdio transport (Server.runWithSubscriptions): cleanup on disconnect is NOT
    /// wired — the SDK does not expose an equivalent hook for stdio as of 1.2.0.
    /// See: https://github.com/Neftedollar/FsMcp/issues/5
    ///
    /// Capture the registry in the caller (typically a mutable cell) so tool
    /// handlers or external triggers can call ResourceSubscriptions.notifyChanged
    /// after the host starts running:
    ///
    ///     let mutable registry = None
    ///     do! HttpServer.runWithSubscriptions config (Some "/mcp") "http://localhost:8080" (fun r ->
    ///         registry <- r
    ///         Task.FromResult(()))
    let runWithSubscriptions
        (config: ServerConfig)
        (endpoint: string option)
        (url: string)
        (onRegistry: FsMcp.Server.ResourceSubscriptionRegistry option -> Task<unit>)
        : Task<unit> =
        task {
            let builder = WebApplication.CreateBuilder()
            builder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

            let mcpBuilder = builder.Services.AddMcpServer()

            let registry = FsMcp.Server.Server.registerAllInternal mcpBuilder config

            // Wire RunSessionHandler so that on session end, per-session subscriptions
            // are cleaned up automatically. The handler calls McpServer.RunAsync and then,
            // in the finally block, removes all subscriptions for the disconnected session.
            // RunSessionHandler signature: Func<HttpContext, McpServer, CancellationToken, Task>
            mcpBuilder.WithHttpTransport(fun opts ->
                match registry with
                | Some reg ->
                    opts.RunSessionHandler <-
                        Func<HttpContext, McpServer, CancellationToken, Task>(
                            fun _ctx server ct ->
                                task {
                                    try
                                        do! server.RunAsync(ct)
                                    finally
                                        let sessionId =
                                            server.SessionId
                                            |> Option.ofObj
                                            |> Option.defaultValue ""
                                        if sessionId <> "" then
                                            ResourceSubscriptions.unsubscribeAllForSession sessionId reg
                                } :> Task)
                | None -> ()) |> ignore

            do! onRegistry registry

            use app = builder.Build()
            let route = endpoint |> Option.defaultValue "/"
            app.MapMcp(route) |> ignore
            app.Urls.Add(url)
            do! app.RunAsync()
        }

    /// Run the MCP server over HTTP as Async.
    let runAsync (config: ServerConfig) (endpoint: string option) (url: string) : Async<unit> =
        run config endpoint url |> Async.AwaitTask
