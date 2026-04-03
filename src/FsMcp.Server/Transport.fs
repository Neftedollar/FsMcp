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
open Microsoft.AspNetCore.Builder

/// Functions for running an MCP server from a ServerConfig.
module Server =

    // ────────────────────────────────────────────────
    //  Tool bridge
    // ────────────────────────────────────────────────

    let private createSdkTool (td: ToolDefinition) : McpServerTool =
        let handler =
            Func<McpServer, Dictionary<string, JsonElement>, Threading.CancellationToken, Task<CallToolResult>>(
                fun _server args _ct ->
                    task {
                        let map =
                            if isNull args then Map.empty
                            else args |> Seq.map (fun kv -> kv.Key, kv.Value.Clone()) |> Map.ofSeq
                        try
                            let! result = td.Handler map
                            match result with
                            | Ok contents ->
                                return CallToolResult(
                                    Content = (contents |> List.map Interop.toSdkContentBlock |> List.toArray))
                            | Error err ->
                                return CallToolResult(
                                    Content = [| TextContentBlock(Text = $"%A{err}") |],
                                    IsError = true)
                        with ex ->
                            return CallToolResult(
                                Content = [| TextContentBlock(Text = ex.Message) |],
                                IsError = true)
                    })
        McpServerTool.Create(
            handler :> Delegate,
            McpServerToolCreateOptions(
                Name = ToolName.value td.Name,
                Description = td.Description))

    // ────────────────────────────────────────────────
    //  Resource bridge
    // ────────────────────────────────────────────────

    let private createSdkResource (rd: ResourceDefinition) : McpServerResource =
        let uriStr = ResourceUri.value rd.Uri
        let handler =
            Func<McpServer, Threading.CancellationToken, Task<ReadResourceResult>>(
                fun _server _ct ->
                    task {
                        let! result = rd.Handler Map.empty
                        match result with
                        | Ok rc ->
                            let sdkRc : ResourceContents =
                                match rc with
                                | FsMcp.Core.TextResource (uri, mime, text) ->
                                    TextResourceContents(
                                        Uri = ResourceUri.value uri,
                                        MimeType = MimeType.value mime,
                                        Text = text) :> ResourceContents
                                | FsMcp.Core.BlobResource (uri, mime, data) ->
                                    BlobResourceContents(
                                        Uri = ResourceUri.value uri,
                                        MimeType = MimeType.value mime,
                                        Blob = ReadOnlyMemory(data)) :> ResourceContents
                            return ReadResourceResult(Contents = [| sdkRc |])
                        | Error err ->
                            return ReadResourceResult(Contents = [| TextResourceContents(
                                Uri = uriStr, Text = $"Error: %A{err}") :> ResourceContents |])
                    })
        let options = McpServerResourceCreateOptions(
            Name = rd.Name,
            Description = (rd.Description |> Option.defaultValue ""),
            UriTemplate = uriStr)
        match rd.MimeType with
        | Some m -> options.MimeType <- MimeType.value m
        | None -> ()
        McpServerResource.Create(handler :> Delegate, options)

    // ────────────────────────────────────────────────
    //  Prompt bridge
    // ────────────────────────────────────────────────

    let private createSdkPrompt (pd: PromptDefinition) : McpServerPrompt =
        let handler =
            Func<McpServer, Dictionary<string, string>, Threading.CancellationToken, Task<GetPromptResult>>(
                fun _server args _ct ->
                    task {
                        let map =
                            if isNull args then Map.empty
                            else args |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                        let! result = pd.Handler map
                        match result with
                        | Ok messages ->
                            let sdkMsgs =
                                messages
                                |> List.map (fun msg ->
                                    PromptMessage(
                                        Role = Interop.toSdkRole msg.Role,
                                        Content = Interop.toSdkContentBlock msg.Content))
                                |> List.toArray
                            return GetPromptResult(
                                Messages = sdkMsgs,
                                Description = (pd.Description |> Option.defaultValue ""))
                        | Error err ->
                            return GetPromptResult(
                                Messages = [| PromptMessage(
                                    Role = Role.Assistant,
                                    Content = TextContentBlock(Text = $"Error: %A{err}")) |])
                    })
        let options = McpServerPromptCreateOptions(
            Name = PromptName.value pd.Name,
            Description = (pd.Description |> Option.defaultValue ""))
        McpServerPrompt.Create(handler :> Delegate, options)

    // ────────────────────────────────────────────────
    //  Registration
    // ────────────────────────────────────────────────

    let private registerAll (builder: IMcpServerBuilder) (config: ServerConfig) =
        if not (List.isEmpty config.Tools) then
            builder.WithTools(config.Tools |> List.map createSdkTool) |> ignore
        if not (List.isEmpty config.Resources) then
            builder.WithResources(
                config.Resources |> List.map createSdkResource :> IEnumerable<McpServerResource>) |> ignore
        if not (List.isEmpty config.Prompts) then
            builder.WithPrompts(config.Prompts |> List.map createSdkPrompt) |> ignore

    // ────────────────────────────────────────────────
    //  Run (stdio)
    // ────────────────────────────────────────────────

    /// Run the MCP server over stdio. Blocks until shutdown.
    let run (config: ServerConfig) : Task<unit> =
        task {
            let hostBuilder = Host.CreateApplicationBuilder()
            hostBuilder.Logging.AddConsole(fun opts ->
                opts.LogToStandardErrorThreshold <- LogLevel.Trace) |> ignore
            hostBuilder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

            let mcpBuilder = hostBuilder.Services.AddMcpServer()
            mcpBuilder.WithStdioServerTransport() |> ignore
            registerAll mcpBuilder config

            do! hostBuilder.Build().RunAsync()
        }

    /// Run the MCP server over stdio as an Async computation.
    let runAsync (config: ServerConfig) : Async<unit> =
        run config |> Async.AwaitTask

    // ────────────────────────────────────────────────
    //  Run (HTTP) — requires ASP.NET Core
    // ────────────────────────────────────────────────

    /// Run the MCP server over HTTP (Streamable HTTP + SSE).
    /// Requires the ModelContextProtocol.AspNetCore package.
    let runHttp (config: ServerConfig) (endpoint: string option) (url: string) : Task<unit> =
        task {
            let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder()
            builder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

            let mcpBuilder = builder.Services.AddMcpServer()
            mcpBuilder.WithHttpTransport() |> ignore
            registerAll mcpBuilder config

            let app = builder.Build()
            let route = endpoint |> Option.defaultValue "/"
            app.MapMcp(route) |> ignore
            app.Urls.Add(url)
            do! app.RunAsync()
        }

    /// Run the MCP server over HTTP as an Async computation.
    let runHttpAsync (config: ServerConfig) (endpoint: string option) (url: string) : Async<unit> =
        runHttp config endpoint url |> Async.AwaitTask
