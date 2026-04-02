namespace FsMcp.Server

open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation

/// Convenience functions for defining MCP handlers.
module Tool =
    /// Define a tool with name, description, and handler.
    let define
        (name: string)
        (description: string)
        (handler: Map<string, JsonElement> -> Task<Result<Content list, McpError>>)
        : Result<ToolDefinition, ValidationError> =
        match ToolName.create name with
        | Ok tn ->
            Ok {
                Name = tn
                Description = description
                InputSchema = None
                Handler = handler
            }
        | Error e -> Error e

module Resource =
    /// Define a resource with URI, name, and handler.
    let define
        (uri: string)
        (name: string)
        (handler: Map<string, string> -> Task<Result<ResourceContents, McpError>>)
        : Result<ResourceDefinition, ValidationError> =
        match ResourceUri.create uri with
        | Ok ru ->
            Ok {
                Uri = ru
                Name = name
                Description = None
                MimeType = None
                Handler = handler
            }
        | Error e -> Error e

module Prompt =
    /// Define a prompt with name, arguments, and handler.
    let define
        (name: string)
        (args: PromptArgument list)
        (handler: Map<string, string> -> Task<Result<McpMessage list, McpError>>)
        : Result<PromptDefinition, ValidationError> =
        match PromptName.create name with
        | Ok pn ->
            Ok {
                Name = pn
                Description = None
                Arguments = args
                Handler = handler
            }
        | Error e -> Error e
