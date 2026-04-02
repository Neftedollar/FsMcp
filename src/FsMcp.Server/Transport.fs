namespace FsMcp.Server

open System
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open FsMcp.Core
open FsMcp.Core.Validation
open ModelContextProtocol.Server
open ModelContextProtocol.Protocol

/// Functions for running an MCP server from a ServerConfig.
module Server =

    /// Create an SDK McpServerTool from an F# ToolDefinition.
    let private createSdkTool (td: ToolDefinition) : McpServerTool =
        let handler =
            Func<McpServer, Dictionary<string, JsonElement>, System.Threading.CancellationToken, Task<CallToolResult>>(
                fun _server args ct ->
                    task {
                        let map =
                            if isNull args then Map.empty
                            else
                                args
                                |> Seq.map (fun kv -> kv.Key, kv.Value.Clone())
                                |> Map.ofSeq
                        let! result = td.Handler map
                        match result with
                        | Ok contents ->
                            let sdkContent =
                                contents
                                |> List.map Interop.toSdkContentBlock
                                |> List.toArray
                            return CallToolResult(Content = sdkContent)
                        | Error err ->
                            let errorText =
                                match err with
                                | McpError.TransportError msg -> msg
                                | McpError.ProtocolError (code, msg) -> $"[{code}] {msg}"
                                | McpError.HandlerException ex -> ex.Message
                                | McpError.ValidationFailed errs -> $"Validation failed: %A{errs}"
                                | McpError.ToolNotFound tn -> $"Tool not found: {ToolName.value tn}"
                                | McpError.ResourceNotFound ru -> $"Resource not found: {ResourceUri.value ru}"
                                | McpError.PromptNotFound pn -> $"Prompt not found: {PromptName.value pn}"
                            return CallToolResult(
                                Content = [| TextContentBlock(Text = errorText) |],
                                IsError = true)
                    })
        let options = McpServerToolCreateOptions(
            Name = ToolName.value td.Name,
            Description = td.Description)
        McpServerTool.Create(handler :> Delegate, options)

    /// Register tools from ServerConfig with the SDK builder.
    let private registerTools (builder: IMcpServerBuilder) (tools: ToolDefinition list) =
        if not (List.isEmpty tools) then
            let sdkTools = tools |> List.map createSdkTool
            builder.WithTools(sdkTools) |> ignore

    /// Run the MCP server with the given configuration.
    /// This starts the server and blocks until it's shut down.
    let run (config: ServerConfig) : Task<unit> =
        task {
            let hostBuilder = Host.CreateApplicationBuilder()

            // Configure logging to stderr for stdio transport
            match config.Transport with
            | Stdio ->
                hostBuilder.Logging.AddConsole(fun options ->
                    options.LogToStandardErrorThreshold <- LogLevel.Trace
                ) |> ignore
            | Http _ ->
                hostBuilder.Logging.AddConsole() |> ignore

            hostBuilder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

            // Configure MCP server
            let mcpBuilder = hostBuilder.Services.AddMcpServer()

            // Configure transport
            match config.Transport with
            | Stdio ->
                mcpBuilder.WithStdioServerTransport() |> ignore
            | Http _ ->
                // Use StreamServerTransport as fallback; HTTP requires ASP.NET integration
                mcpBuilder.WithStdioServerTransport() |> ignore

            // Register tools
            registerTools mcpBuilder config.Tools

            // Build and run
            let host = hostBuilder.Build()
            do! host.RunAsync()
        }

    /// Run the MCP server as an Async computation.
    let runAsync (config: ServerConfig) : Async<unit> =
        run config |> Async.AwaitTask
