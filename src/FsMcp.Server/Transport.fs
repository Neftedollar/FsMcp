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

    // ────────────────────────────────────────────────
    //  Tool bridge
    // ────────────────────────────────────────────────

    /// Custom AIFunction that bridges FsMcp's handler to the MCP SDK.
    /// Avoids reflection-based parameter binding by providing schema + direct invocation.
    type private ToolAIFunction(td: ToolDefinition) =
        inherit Microsoft.Extensions.AI.AIFunction()

        let schema =
            match td.InputSchema with
            | Some s -> s
            | None ->
                use doc = JsonDocument.Parse("""{"type":"object","properties":{}}""")
                doc.RootElement.Clone()

        override _.Name = ToolName.value td.Name
        override _.Description = td.Description
        override _.JsonSchema = schema
        override _.InvokeCoreAsync(arguments, _ct) =
            ValueTask<obj>(task {
                let map =
                    if isNull arguments then Map.empty
                    else
                        arguments
                        |> Seq.choose (fun kv ->
                            match kv.Value with
                            | :? JsonElement as je -> Some(kv.Key, je.Clone())
                            | v when not (isNull v) ->
                                let je = JsonSerializer.SerializeToElement(v)
                                Some(kv.Key, je)
                            | _ -> None)
                        |> Map.ofSeq
                try
                    let! result = td.Handler map
                    match result with
                    | Ok contents ->
                        return CallToolResult(
                            Content = (contents |> List.map Interop.toSdkContentBlock |> List.toArray)) :> obj
                    | Error err ->
                        return CallToolResult(
                            Content = [| TextContentBlock(Text = $"%A{err}") |],
                            IsError = true) :> obj
                with ex ->
                    return CallToolResult(
                        Content = [| TextContentBlock(Text = ex.Message) |],
                        IsError = true) :> obj
            })

    let private createSdkTool (td: ToolDefinition) : McpServerTool =
        let aiFunction = ToolAIFunction(td)
        McpServerTool.Create(
            aiFunction,
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

    /// Register all tools, resources, and prompts from a ServerConfig.
    /// When the config has subscribable resources, also registers subscribe/unsubscribe
    /// handlers and returns a populated ResourceSubscriptionRegistry.
    /// Returns None when there are no resources (no subscription wiring needed).
    let internal registerAllInternal (builder: IMcpServerBuilder) (config: ServerConfig) : ResourceSubscriptionRegistry option =
        if not (List.isEmpty config.Tools) then
            builder.WithTools(config.Tools |> List.map createSdkTool) |> ignore
        if not (List.isEmpty config.Resources) then
            builder.WithResources(
                config.Resources |> List.map createSdkResource :> IEnumerable<McpServerResource>) |> ignore
        if not (List.isEmpty config.Prompts) then
            builder.WithPrompts(config.Prompts |> List.map createSdkPrompt) |> ignore

        // Wire subscribe/unsubscribe handlers if there are resources (B1, B2, B3)
        if not (List.isEmpty config.Resources) then
            let registry = ResourceSubscriptions.create ()

            let subscribeHandler =
                McpRequestHandler<ModelContextProtocol.Protocol.SubscribeRequestParams, ModelContextProtocol.Protocol.EmptyResult>(
                    fun req _ct ->
                        ValueTask<ModelContextProtocol.Protocol.EmptyResult>(task {
                            let uriStr = if isNull req.Params then "" else req.Params.Uri
                            let server = req.Server
                            let sessionId =
                                if isNull server then ""
                                else server.SessionId |> Option.ofObj |> Option.defaultValue ""
                            match ResourceUri.create uriStr with
                            | Ok uri ->
                                // Track the per-session McpServer for notification dispatch
                                registry.SessionServers.[sessionId] <- server
                                ResourceSubscriptions.subscribe sessionId uri registry |> ignore
                            | Error _ -> () // Ignore invalid URIs silently
                            return ModelContextProtocol.Protocol.EmptyResult()
                        }))

            let unsubscribeHandler =
                McpRequestHandler<ModelContextProtocol.Protocol.UnsubscribeRequestParams, ModelContextProtocol.Protocol.EmptyResult>(
                    fun req _ct ->
                        ValueTask<ModelContextProtocol.Protocol.EmptyResult>(task {
                            let uriStr = if isNull req.Params then "" else req.Params.Uri
                            let sessionId =
                                if isNull req.Server then ""
                                else req.Server.SessionId |> Option.ofObj |> Option.defaultValue ""
                            match ResourceUri.create uriStr with
                            | Ok uri ->
                                // Remove the specific (session, uri) subscription(s)
                                let toRemove =
                                    registry.Subscribers
                                    |> Seq.filter (fun kv ->
                                        kv.Value.SessionId = sessionId && kv.Value.Uri = uri)
                                    |> Seq.map (fun kv -> kv.Key)
                                    |> Seq.toList
                                for id in toRemove do
                                    ResourceSubscriptions.unsubscribe id registry
                            | Error _ -> ()
                            return ModelContextProtocol.Protocol.EmptyResult()
                        }))

            builder.WithSubscribeToResourcesHandler(subscribeHandler) |> ignore
            builder.WithUnsubscribeFromResourcesHandler(unsubscribeHandler) |> ignore
            Some registry
        else
            None

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
            registerAllInternal mcpBuilder config |> ignore

            use host = hostBuilder.Build()
            do! host.RunAsync()
        }

    /// Run the MCP server over stdio and expose the subscription registry.
    /// The onRegistry callback fires once, before the host starts, with the registry
    /// (or None when the config has no resources). The host then runs to completion.
    ///
    /// Capture the registry in the caller so tool handlers can later trigger
    /// notifyChanged. The typical pattern is a mutable cell or ref:
    ///
    ///     let mutable registry = None
    ///     do! Server.runWithSubscriptions config (fun r ->
    ///         registry <- r
    ///         Task.FromResult(()))
    ///     // From a tool handler that mutates a watched resource:
    ///     // registry |> Option.iter (ResourceSubscriptions.notifyChanged uri >> ignore)
    let runWithSubscriptions
        (config: ServerConfig)
        (onRegistry: ResourceSubscriptionRegistry option -> Task<unit>)
        : Task<unit> =
        task {
            let hostBuilder = Host.CreateApplicationBuilder()
            hostBuilder.Logging.AddConsole(fun opts ->
                opts.LogToStandardErrorThreshold <- LogLevel.Trace) |> ignore
            hostBuilder.Logging.SetMinimumLevel(LogLevel.Information) |> ignore

            let mcpBuilder = hostBuilder.Services.AddMcpServer()
            mcpBuilder.WithStdioServerTransport() |> ignore
            let registry = registerAllInternal mcpBuilder config

            do! onRegistry registry

            use host = hostBuilder.Build()
            do! host.RunAsync()
        }

    /// Run the MCP server over stdio as an Async computation.
    let runAsync (config: ServerConfig) : Async<unit> =
        run config |> Async.AwaitTask

    // HTTP transport is in the separate FsMcp.Server.Http package.
    // Install it only if you need HTTP/SSE — stdio doesn't require ASP.NET.
