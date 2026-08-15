namespace FsMcp.Testing

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

/// Simplified tool information for test queries.
type ToolInfo = {
    Name: string
    Description: string
}

/// Simplified resource information for test queries.
type ResourceInfo = {
    Uri: string
    Name: string
    Description: string option
    MimeType: string option
}

/// Simplified prompt information for test queries.
type PromptInfo = {
    Name: string
    Description: string option
    Arguments: PromptArgument list
}

/// Functions for testing MCP server configurations by invoking handlers directly,
/// bypassing transport and protocol overhead.
module TestServer =

    /// Call a tool handler from the ServerConfig by name, propagating cancellation.
    let callToolWithCancellation
        (config: ServerConfig)
        (toolName: string)
        (args: Map<string, JsonElement>)
        (cancellationToken: CancellationToken)
        : Task<Result<Content list, McpError>> =
        task {
            let tool =
                config.Tools
                |> List.tryFind (fun t -> ToolName.value t.Name = toolName)
            match tool with
            | Some td ->
                try
                    return! td.Handler args cancellationToken
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | ex ->
                    return Error (HandlerException ex)
            | None ->
                match ToolName.create toolName with
                | Ok tn -> return Error (ToolNotFound tn)
                | Error _ ->
                    // If the name itself is invalid, still return ToolNotFound
                    // with a best-effort name. Since ToolName.create validates,
                    // fall back to creating the error with a placeholder.
                    // However, an empty/whitespace name would fail. For test ergonomics
                    // we handle this edge case directly.
                    return Error (TransportError $"Invalid tool name: '{toolName}'")
        }

    /// Call a tool with a non-cancellable test context.
    let callTool config toolName args =
        callToolWithCancellation config toolName args CancellationToken.None

    /// Read a resource handler from the ServerConfig by URI, propagating cancellation.
    let readResourceWithCancellation
        (config: ServerConfig)
        (uri: string)
        (args: Map<string, string>)
        (cancellationToken: CancellationToken)
        : Task<Result<ResourceContents, McpError>> =
        task {
            let resource =
                config.Resources
                |> List.tryFind (fun r -> ResourceUri.value r.Uri = uri)
            match resource with
            | Some rd ->
                try
                    return! rd.Handler args cancellationToken
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | ex ->
                    return Error (HandlerException ex)
            | None ->
                match ResourceUri.create uri with
                | Ok ru -> return Error (ResourceNotFound ru)
                | Error _ ->
                    return Error (TransportError $"Invalid resource URI: '{uri}'")
        }

    /// Read a resource with a non-cancellable test context.
    let readResource config uri args =
        readResourceWithCancellation config uri args CancellationToken.None

    /// Get a prompt handler from the ServerConfig by name, propagating cancellation.
    let getPromptWithCancellation
        (config: ServerConfig)
        (promptName: string)
        (args: Map<string, string>)
        (cancellationToken: CancellationToken)
        : Task<Result<McpMessage list, McpError>> =
        task {
            let prompt =
                config.Prompts
                |> List.tryFind (fun p -> PromptName.value p.Name = promptName)
            match prompt with
            | Some pd ->
                try
                    return! pd.Handler args cancellationToken
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return raise (OperationCanceledException(cancellationToken))
                | ex ->
                    return Error (HandlerException ex)
            | None ->
                match PromptName.create promptName with
                | Ok pn -> return Error (PromptNotFound pn)
                | Error _ ->
                    return Error (TransportError $"Invalid prompt name: '{promptName}'")
        }

    /// Get a prompt with a non-cancellable test context.
    let getPrompt config promptName args =
        getPromptWithCancellation config promptName args CancellationToken.None

    /// List all tools in the ServerConfig.
    let listTools (config: ServerConfig) : ToolInfo list =
        config.Tools
        |> List.map (fun t ->
            { ToolInfo.Name = ToolName.value t.Name
              Description = t.Description })

    /// List all resources in the ServerConfig.
    let listResources (config: ServerConfig) : ResourceInfo list =
        config.Resources
        |> List.map (fun r ->
            { ResourceInfo.Uri = ResourceUri.value r.Uri
              Name = r.Name
              Description = r.Description
              MimeType = r.MimeType |> Option.map MimeType.value })

    /// List all prompts in the ServerConfig.
    let listPrompts (config: ServerConfig) : PromptInfo list =
        config.Prompts
        |> List.map (fun p ->
            { PromptInfo.Name = PromptName.value p.Name
              Description = p.Description
              Arguments = p.Arguments })
