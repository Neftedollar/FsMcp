namespace FsMcp.Client

open System
open System.Collections.Generic
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open ModelContextProtocol.Client

/// Simplified tool information returned from listing tools.
type ToolInfo = {
    Name: string
    Description: string
}

/// Simplified resource information returned from listing resources.
type ResourceInfo = {
    Uri: string
    Name: string
    MimeType: string option
}

/// Simplified prompt information returned from listing prompts.
type PromptInfo = {
    Name: string
    Description: string option
}

/// Client configuration.
type ClientConfig = {
    Transport: ClientTransport
    Name: string
    ShutdownTimeout: TimeSpan option
}

/// Wrapper around the C# SDK's McpClient.
type McpClient = internal {
    Client: ModelContextProtocol.Client.McpClient
}

/// Functions for creating and interacting with MCP clients.
module McpClient =

    /// Build an SDK transport from our ClientTransport DU.
    let private buildTransport (transport: ClientTransport) : IClientTransport =
        match transport with
        | StdioProcess (command, args) ->
            let options = StdioClientTransportOptions(Command = command)
            options.Arguments <- args |> List.toArray
            StdioClientTransport(options) :> IClientTransport
        | HttpEndpoint (uri, headers) ->
            let options = HttpClientTransportOptions(Endpoint = uri)
            if not (Map.isEmpty headers) then
                let dict = Dictionary<string, string>()
                headers |> Map.iter (fun k v -> dict.[k] <- v)
                options.AdditionalHeaders <- dict
            HttpClientTransport(options) :> IClientTransport

    /// Convert an SDK ContentBlock to our Content type.
    let private convertContentBlock (block: ModelContextProtocol.Protocol.ContentBlock) : Content =
        match block with
        | :? ModelContextProtocol.Protocol.TextContentBlock as t ->
            Content.Text t.Text
        | :? ModelContextProtocol.Protocol.ImageContentBlock as img ->
            let mimeType = if isNull img.MimeType then "image/png" else img.MimeType
            let data = img.DecodedData.ToArray()
            match MimeType.create mimeType with
            | Ok m -> Content.Image (data, m)
            | Error _ -> Content.Text "[image with invalid mime type]"
        | :? ModelContextProtocol.Protocol.EmbeddedResourceBlock as res ->
            let rc = res.Resource
            match rc with
            | :? ModelContextProtocol.Protocol.TextResourceContents as trc ->
                let uri = ResourceUri.create (if isNull trc.Uri then "" else trc.Uri)
                let mime = MimeType.create (if isNull trc.MimeType then "" else trc.MimeType)
                match uri, mime with
                | Ok u, Ok m ->
                    Content.EmbeddedResource (TextResource (u, m, trc.Text))
                | _ ->
                    Content.Text (if isNull trc.Text then "" else trc.Text)
            | :? ModelContextProtocol.Protocol.BlobResourceContents as brc ->
                let uri = ResourceUri.create (if isNull brc.Uri then "" else brc.Uri)
                let mime = MimeType.create (if isNull brc.MimeType then "" else brc.MimeType)
                let data = brc.DecodedData.ToArray()
                match uri, mime with
                | Ok u, Ok m ->
                    Content.EmbeddedResource (BlobResource (u, m, data))
                | _ ->
                    Content.Text "[binary resource]"
            | _ ->
                Content.Text "[unknown resource type]"
        | _ ->
            Content.Text "[unsupported content type]"

    /// Convert an SDK Role to our McpRole.
    let private convertRole (role: ModelContextProtocol.Protocol.Role) : McpRole =
        match role with
        | ModelContextProtocol.Protocol.Role.User -> McpRole.User
        | ModelContextProtocol.Protocol.Role.Assistant -> McpRole.Assistant
        | _ -> McpRole.User

    /// Connect to an MCP server.
    let connect (config: ClientConfig) : Task<McpClient> =
        task {
            let transport = buildTransport config.Transport
            let options = McpClientOptions()
            options.ClientInfo <- ModelContextProtocol.Protocol.Implementation(Name = config.Name, Version = "1.0.0")
            let! client =
                ModelContextProtocol.Client.McpClient.CreateAsync(
                    transport,
                    options
                )
            return { Client = client }
        }

    /// List available tools.
    let listTools (client: McpClient) : Task<ToolInfo list> =
        task {
            let! tools = client.Client.ListToolsAsync()
            return
                tools
                |> Seq.map (fun t ->
                    { ToolInfo.Name = t.Name
                      Description = if isNull t.Description then "" else t.Description })
                |> Seq.toList
        }

    /// Call a tool by name with arguments.
    let callTool (client: McpClient) (toolName: ToolName) (args: Map<string, System.Text.Json.JsonElement>) : Task<Result<Content list, McpError>> =
        task {
            try
                let dict = Dictionary<string, obj>()
                args |> Map.iter (fun k v -> dict.[k] <- (v :> obj))
                let! result =
                    client.Client.CallToolAsync(
                        ToolName.value toolName,
                        dict
                    )
                let isError = result.IsError.GetValueOrDefault(false)
                if isError then
                    let errorText =
                        result.Content
                        |> Seq.tryPick (fun c ->
                            match c with
                            | :? ModelContextProtocol.Protocol.TextContentBlock as t -> Some t.Text
                            | _ -> None)
                        |> Option.defaultValue "Tool call failed"
                    return Error (TransportError errorText)
                else
                    let contents =
                        result.Content
                        |> Seq.map convertContentBlock
                        |> Seq.toList
                    return Ok contents
            with
            | :? ModelContextProtocol.McpException as ex ->
                return Error (ProtocolError (0, ex.Message))
            | ex ->
                return Error (HandlerException ex)
        }

    /// List available resources.
    let listResources (client: McpClient) : Task<ResourceInfo list> =
        task {
            let! resources = client.Client.ListResourcesAsync()
            return
                resources
                |> Seq.map (fun r ->
                    { ResourceInfo.Uri = if isNull r.Uri then "" else r.Uri
                      Name = if isNull r.Name then "" else r.Name
                      MimeType = if isNull r.MimeType then None else Some r.MimeType })
                |> Seq.toList
        }

    /// Read a resource by URI.
    let readResource (client: McpClient) (uri: ResourceUri) : Task<Result<FsMcp.Core.ResourceContents, McpError>> =
        task {
            try
                let! result = client.Client.ReadResourceAsync(ResourceUri.value uri)
                let content =
                    result.Contents
                    |> Seq.tryHead
                match content with
                | Some (:? ModelContextProtocol.Protocol.TextResourceContents as trc) ->
                    let ru = ResourceUri.create (if isNull trc.Uri then ResourceUri.value uri else trc.Uri)
                    let mime = MimeType.create (if isNull trc.MimeType then "" else trc.MimeType)
                    match ru, mime with
                    | Ok u, Ok m ->
                        return Ok (TextResource (u, m, trc.Text))
                    | _ ->
                        return Error (TransportError "Invalid resource URI or MIME type in response")
                | Some (:? ModelContextProtocol.Protocol.BlobResourceContents as brc) ->
                    let ru = ResourceUri.create (if isNull brc.Uri then ResourceUri.value uri else brc.Uri)
                    let mime = MimeType.create (if isNull brc.MimeType then "" else brc.MimeType)
                    let data = brc.DecodedData.ToArray()
                    match ru, mime with
                    | Ok u, Ok m ->
                        return Ok (BlobResource (u, m, data))
                    | _ ->
                        return Error (TransportError "Invalid resource URI or MIME type in response")
                | Some _ ->
                    return Error (TransportError "Unsupported resource content type")
                | None ->
                    return Error (ResourceNotFound uri)
            with
            | :? ModelContextProtocol.McpException as ex ->
                return Error (ProtocolError (0, ex.Message))
            | ex ->
                return Error (HandlerException ex)
        }

    /// List available prompts.
    let listPrompts (client: McpClient) : Task<PromptInfo list> =
        task {
            let! prompts = client.Client.ListPromptsAsync()
            return
                prompts
                |> Seq.map (fun p ->
                    { PromptInfo.Name = if isNull p.Name then "" else p.Name
                      Description = if isNull p.Description then None else Some p.Description })
                |> Seq.toList
        }

    /// Get a prompt with arguments.
    let getPrompt
        (client: McpClient)
        (promptName: PromptName)
        (args: Map<string, string>)
        : Task<Result<McpMessage list, McpError>> =
        task {
            try
                let dict = Dictionary<string, obj>()
                args |> Map.iter (fun k v -> dict.[k] <- (v :> obj))
                let! result =
                    client.Client.GetPromptAsync(
                        PromptName.value promptName,
                        dict
                    )
                let messages =
                    result.Messages
                    |> Seq.map (fun m ->
                        { Role = convertRole m.Role
                          Content = convertContentBlock m.Content })
                    |> Seq.toList
                return Ok messages
            with
            | :? ModelContextProtocol.McpException as ex ->
                return Error (ProtocolError (0, ex.Message))
            | ex ->
                return Error (HandlerException ex)
        }

    /// Disconnect and dispose.
    let disconnect (client: McpClient) : Task<unit> =
        task {
            do! client.Client.DisposeAsync()
        }

/// Async wrappers for all McpClient functions.
module McpClientAsync =

    /// Connect to an MCP server.
    let connect (config: ClientConfig) : Async<McpClient> =
        McpClient.connect config |> Async.AwaitTask

    /// List available tools.
    let listTools (client: McpClient) : Async<ToolInfo list> =
        McpClient.listTools client |> Async.AwaitTask

    /// Call a tool by name with arguments.
    let callTool (client: McpClient) (toolName: ToolName) (args: Map<string, System.Text.Json.JsonElement>) : Async<Result<Content list, McpError>> =
        McpClient.callTool client toolName args |> Async.AwaitTask

    /// List available resources.
    let listResources (client: McpClient) : Async<ResourceInfo list> =
        McpClient.listResources client |> Async.AwaitTask

    /// Read a resource by URI.
    let readResource (client: McpClient) (uri: ResourceUri) : Async<Result<FsMcp.Core.ResourceContents, McpError>> =
        McpClient.readResource client uri |> Async.AwaitTask

    /// List available prompts.
    let listPrompts (client: McpClient) : Async<PromptInfo list> =
        McpClient.listPrompts client |> Async.AwaitTask

    /// Get a prompt with arguments.
    let getPrompt (client: McpClient) (promptName: PromptName) (args: Map<string, string>) : Async<Result<McpMessage list, McpError>> =
        McpClient.getPrompt client promptName args |> Async.AwaitTask

    /// Disconnect and dispose.
    let disconnect (client: McpClient) : Async<unit> =
        McpClient.disconnect client |> Async.AwaitTask
