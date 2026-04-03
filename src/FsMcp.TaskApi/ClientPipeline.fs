namespace FsMcp.TaskApi

open System.Text.Json
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Client

/// Pipe-friendly MCP client operations for use with taskResult { } CE.
///
/// Example:
///   open FsMcp.TaskApi
///   open FsToolkit.ErrorHandling
///
///   taskResult {
///       let! client = ClientPipeline.connect config
///       let! text = client |> ClientPipeline.callToolText "greet" (Map.ofList ["name", jsonEl])
///       printfn "%s" text
///       do! client |> ClientPipeline.disconnect
///   }
module ClientPipeline =

    /// Connect to an MCP server. Wraps result in Ok for taskResult chains.
    let connect (config: ClientConfig) : Task<Result<McpClient, McpError>> =
        task {
            try
                let! client = McpClient.connect config
                return Ok client
            with ex ->
                return Error (HandlerException ex)
        }

    /// List tools from a connected client.
    let listTools (client: McpClient) : Task<Result<ToolInfo list, McpError>> =
        task {
            try
                let! tools = McpClient.listTools client
                return Ok tools
            with ex ->
                return Error (HandlerException ex)
        }

    /// Call a tool by string name (validates the name internally).
    let callTool
        (name: string)
        (args: Map<string, JsonElement>)
        (client: McpClient)
        : Task<Result<Content list, McpError>> =
        taskResult {
            let! toolName =
                ToolName.create name
                |> Result.mapError (fun e -> ValidationFailed [e])
            return! McpClient.callTool client toolName args
        }

    /// Call a tool and extract the first text content as a string.
    let callToolText
        (name: string)
        (args: Map<string, JsonElement>)
        (client: McpClient)
        : Task<Result<string, McpError>> =
        taskResult {
            let! contents = callTool name args client
            return
                contents
                |> List.tryPick (function Text t -> Some t | _ -> None)
                |> Option.defaultValue ""
        }

    /// Read a resource by string URI (validates internally).
    let readResource
        (uri: string)
        (client: McpClient)
        : Task<Result<ResourceContents, McpError>> =
        taskResult {
            let! resourceUri =
                ResourceUri.create uri
                |> Result.mapError (fun e -> ValidationFailed [e])
            return! McpClient.readResource client resourceUri
        }

    /// Get a prompt by string name (validates internally).
    let getPrompt
        (name: string)
        (args: Map<string, string>)
        (client: McpClient)
        : Task<Result<McpMessage list, McpError>> =
        taskResult {
            let! promptName =
                PromptName.create name
                |> Result.mapError (fun e -> ValidationFailed [e])
            return! McpClient.getPrompt client promptName args
        }

    /// List resources from a connected client.
    let listResources (client: McpClient) : Task<Result<ResourceInfo list, McpError>> =
        task {
            try
                let! resources = McpClient.listResources client
                return Ok resources
            with ex ->
                return Error (HandlerException ex)
        }

    /// List prompts from a connected client.
    let listPrompts (client: McpClient) : Task<Result<PromptInfo list, McpError>> =
        task {
            try
                let! prompts = McpClient.listPrompts client
                return Ok prompts
            with ex ->
                return Error (HandlerException ex)
        }

    /// Disconnect and dispose the client.
    let disconnect (client: McpClient) : Task<Result<unit, McpError>> =
        task {
            try
                do! McpClient.disconnect client
                return Ok ()
            with ex ->
                return Error (HandlerException ex)
        }
