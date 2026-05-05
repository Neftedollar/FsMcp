namespace FsMcp.Server.Http

open System.Threading.Tasks
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
            mcpBuilder.WithHttpTransport() |> ignore

            let registry = FsMcp.Server.Server.registerAllInternal mcpBuilder config

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
