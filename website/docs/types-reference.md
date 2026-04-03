---
sidebar_position: 8
---


# Types Reference

All public domain types in FsMcp with their definitions, constructors, and usage patterns.

## Content types

### `Content`

Content carried by tool results, resource reads, and prompt messages:

```fsharp
type Content =
    | Text of text: string
    | Image of data: byte[] * mimeType: MimeType
    | EmbeddedResource of resource: ResourceContents
```

Convenience constructors:

```fsharp
Content.text "hello"                                    // Text
Content.image [| 0x89uy; 0x50uy |] mimeType            // Image
Content.embeddedResource (TextResource (uri, mime, "")) // EmbeddedResource
```

### `ResourceContents`

Resource data -- either text with a MIME type or raw binary:

```fsharp
type ResourceContents =
    | TextResource of uri: ResourceUri * mimeType: MimeType * text: string
    | BlobResource of uri: ResourceUri * mimeType: MimeType * data: byte[]
```

## Message types

### `McpRole`

```fsharp
type McpRole =
    | User
    | Assistant
```

### `McpMessage`

```fsharp
type McpMessage = {
    Role: McpRole
    Content: Content
}
```

## Error types

### `McpError`

All errors that can occur in the FsMcp toolkit:

```fsharp
type McpError =
    | ValidationFailed of errors: ValidationError list
    | ToolNotFound of name: ToolName
    | ResourceNotFound of uri: ResourceUri
    | PromptNotFound of name: PromptName
    | HandlerException of exn: exn
    | TransportError of message: string
    | ProtocolError of code: int * message: string
```

### `ValidationError`

Errors from smart constructor validation:

```fsharp
type ValidationError =
    | EmptyValue of fieldName: string
    | InvalidFormat of fieldName: string * value: string * expected: string
    | DuplicateEntry of entryType: string * name: string
```

Format as a human-readable string:

```fsharp
ValidationError.format (EmptyValue "ToolName")
// "ToolName cannot be empty"

ValidationError.format (InvalidFormat ("ResourceUri", "bad", "valid absolute URI with scheme"))
// "ResourceUri 'bad' is invalid, expected valid absolute URI with scheme"

ValidationError.format (DuplicateEntry ("Tool", "echo"))
// "Duplicate Tool: 'echo'"
```

## Definition types

### `ToolDefinition`

```fsharp
type ToolDefinition = {
    Name: ToolName
    Description: string
    InputSchema: JsonElement option
    Handler: Map<string, JsonElement> -> Task<Result<Content list, McpError>>
}
```

### `ResourceDefinition`

```fsharp
type ResourceDefinition = {
    Uri: ResourceUri
    Name: string
    Description: string option
    MimeType: MimeType option
    Handler: Map<string, string> -> Task<Result<ResourceContents, McpError>>
}
```

### `PromptDefinition`

```fsharp
type PromptDefinition = {
    Name: PromptName
    Description: string option
    Arguments: PromptArgument list
    Handler: Map<string, string> -> Task<Result<McpMessage list, McpError>>
}
```

### `PromptArgument`

```fsharp
type PromptArgument = {
    Name: string
    Description: string option
    Required: bool
}
```

## Identifier types (smart constructors)

All identifier types are single-case DUs with private constructors. They guarantee validity at construction time.

### `ToolName`

A validated, non-empty, trimmed tool name.

```fsharp
// Create (validates)
let tn : Result<ToolName, ValidationError> = ToolName.create "my-tool"

// Extract the raw string
let s : string = ToolName.value tn

// Typical usage
let toolName = ToolName.create "echo" |> unwrapResult
```

### `ResourceUri`

A validated absolute URI string with a scheme:

```fsharp
let uri = ResourceUri.create "https://example.com/resource"   // Ok
let uri2 = ResourceUri.create "file:///tmp/test.txt"          // Ok
let uri3 = ResourceUri.create "not-a-uri"                     // Error

let s = ResourceUri.value uri  // "https://example.com/resource"
```

### `PromptName`

A validated, non-empty, trimmed prompt name:

```fsharp
let pn = PromptName.create "summarize" |> unwrapResult
let s = PromptName.value pn  // "summarize"
```

### `MimeType`

A validated MIME type string. Empty or null defaults to `"application/octet-stream"`:

```fsharp
let mime = MimeType.create "text/plain" |> unwrapResult
let mime2 = MimeType.create "" |> unwrapResult          // application/octet-stream
let mime3 = MimeType.create "invalid" |> unwrapResult   // Error

let s = MimeType.value mime  // "text/plain"
MimeType.defaultMimeType     // "application/octet-stream"
```

### `ServerName`

A validated, non-empty server name:

```fsharp
let sn = ServerName.create "MyServer" |> unwrapResult
let s = ServerName.value sn
```

### `ServerVersion`

A validated, non-empty server version:

```fsharp
let sv = ServerVersion.create "1.0.0" |> unwrapResult
let s = ServerVersion.value sv
```

## Smart constructor patterns

Every identifier type follows the same pattern:

```fsharp
// ModuleName.create : string -> Result<Type, ValidationError>
// ModuleName.value  : Type -> string
```

`create` validates and returns `Result`. `value` extracts the inner string. Use `unwrapResult` to convert `Result` to a value or throw with a formatted message:

```fsharp
let unwrapResult (result: Result<'T, ValidationError>) : 'T =
    match result with
    | Ok v -> v
    | Error e -> failwith (ValidationError.format e)
```

`unwrapResult` is `[<AutoOpen>]` in the `FsMcp.Core` namespace, so it is available everywhere you open `FsMcp.Core`.

## Server configuration types

### `ServerConfig`

```fsharp
type ServerConfig = {
    Name: ServerName
    Version: ServerVersion
    Tools: ToolDefinition list
    Resources: ResourceDefinition list
    Prompts: PromptDefinition list
    Middleware: McpMiddleware list
    Transport: Transport
}
```

### `Transport`

```fsharp
type Transport =
    | Stdio
    | Http of endpoint: string option
```

### `McpMiddleware`

```fsharp
type McpMiddleware =
    McpContext -> (McpContext -> Task<McpResponse>) -> Task<McpResponse>
```

### `McpContext`

```fsharp
type McpContext = {
    Method: string
    Params: JsonElement option
    CancellationToken: CancellationToken
}
```

### `McpResponse`

```fsharp
type McpResponse =
    | Success of JsonElement
    | McpResponseError of McpError
```

## Namespace summary

| Namespace | Contains |
|---|---|
| `FsMcp.Core` | `Content`, `ResourceContents`, `McpRole`, `McpMessage`, `McpError`, `ToolDefinition`, `ResourceDefinition`, `PromptDefinition`, `PromptArgument`, `unwrapResult` |
| `FsMcp.Core.Validation` | `ValidationError`, `ToolName`, `ResourceUri`, `PromptName`, `MimeType`, `ServerName`, `ServerVersion` |
| `FsMcp.Server` | `ServerConfig`, `Transport`, `McpMiddleware`, `McpContext`, `McpResponse`, `FsMcpConfigException`, `McpServerBuilder`, `mcpServer`, `mcpTool`, `Tool`, `Resource`, `Prompt`, `TypedTool`, `TypedResource`, `TypedPrompt`, `TypedHandler`, `StreamingTool`, `Middleware`, `ValidationMiddleware`, `Telemetry`, `DynamicServer`, `Notifications` |
| `FsMcp.Client` | `ClientTransport`, `ClientConfig`, `McpClient`, `McpClientAsync`, `ToolInfo`, `ResourceInfo`, `PromptInfo` |
| `FsMcp.TaskApi` | `ClientPipeline` |
| `FsMcp.Testing` | `TestServer`, `Expect`, `McpArbitraries` |
| `FsMcp.Sampling` | `SamplingRequest`, `SamplingResult`, `SamplingError`, `SamplingContext`, `SamplingTool`, `SampleFunc` |
| `FsMcp.Server.Http` | `HttpServer` |
