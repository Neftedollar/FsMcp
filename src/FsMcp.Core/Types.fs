namespace FsMcp.Core

open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core.Validation

/// Resource contents — either text with a MIME type or raw binary data.
type ResourceContents =
    | TextResource of uri: ResourceUri * mimeType: MimeType * text: string
    | BlobResource of uri: ResourceUri * mimeType: MimeType * data: byte[]

/// Content carried by tool results, resource reads, and prompt messages.
type Content =
    | Text of text: string
    | Image of data: byte[] * mimeType: MimeType
    | EmbeddedResource of resource: ResourceContents

/// Convenience constructors for Content.
module Content =
    /// Create a Text content from a string.
    let text (s: string) : Content = Text s

    /// Create an Image content from raw bytes and a MIME type.
    let image (data: byte[]) (mimeType: MimeType) : Content = Image (data, mimeType)

    /// Create an EmbeddedResource content from a ResourceContents value.
    let embeddedResource (resource: ResourceContents) : Content = EmbeddedResource resource

/// The role of a participant in an MCP conversation.
type McpRole =
    | User
    | Assistant

/// A message in an MCP conversation.
type McpMessage = {
    Role: McpRole
    Content: Content
}

/// Errors that can occur in the FsMcp toolkit.
[<NoComparison>]
type McpError =
    | ValidationFailed of errors: ValidationError list
    | ToolNotFound of name: ToolName
    | ResourceNotFound of uri: ResourceUri
    | PromptNotFound of name: PromptName
    | HandlerException of exn: exn
    | TransportError of message: string
    | ProtocolError of code: int * message: string

/// A tool definition with a name, description, optional input schema, and handler.
[<NoComparison; NoEquality>]
type ToolDefinition = {
    Name: ToolName
    Description: string
    InputSchema: JsonElement option
    Handler: Map<string, JsonElement> -> Task<Result<Content list, McpError>>
}

/// A resource definition with a URI, name, description, MIME type, and handler.
[<NoComparison; NoEquality>]
type ResourceDefinition = {
    Uri: ResourceUri
    Name: string
    Description: string option
    MimeType: MimeType option
    Handler: Map<string, string> -> Task<Result<ResourceContents, McpError>>
}

/// An argument definition for a prompt.
type PromptArgument = {
    Name: string
    Description: string option
    Required: bool
}

/// A prompt definition with a name, description, argument list, and handler.
[<NoComparison; NoEquality>]
type PromptDefinition = {
    Name: PromptName
    Description: string option
    Arguments: PromptArgument list
    Handler: Map<string, string> -> Task<Result<McpMessage list, McpError>>
}

/// Empty module to ensure this file compiles as a valid F# source.
module Types =
    // Domain types are defined above at the namespace level.
    // This module exists only to satisfy fsproj compilation requirements.
    ()
