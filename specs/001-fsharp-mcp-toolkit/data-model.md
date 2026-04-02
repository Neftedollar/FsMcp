# Data Model: F# MCP Toolkit

## FsMcp.Core — Domain Types

### Identifier Types (single-case DUs)

```text
ToolName        = ToolName of string       (non-empty, trimmed)
ResourceUri     = ResourceUri of string    (valid URI format)
PromptName      = PromptName of string     (non-empty, trimmed)
MimeType        = MimeType of string       (valid MIME format or empty for default)
ServerName      = ServerName of string     (non-empty)
ServerVersion   = ServerVersion of string  (non-empty)
```

All created via smart constructors returning
`Result<'T, ValidationError>`.

### ValidationError

```text
ValidationError
  = EmptyValue of fieldName: string
  | InvalidFormat of fieldName: string * value: string * expected: string
  | DuplicateEntry of entryType: string * name: string
```

### Content (DU — maps to MCP content blocks)

```text
Content
  = Text of text: string
  | Image of data: byte[] * mimeType: MimeType
  | EmbeddedResource of resource: ResourceContents
```

### ResourceContents (DU)

```text
ResourceContents
  = TextResource of uri: ResourceUri * mimeType: MimeType * text: string
  | BlobResource of uri: ResourceUri * mimeType: MimeType * data: byte[]
```

### McpRole

```text
McpRole
  = User
  | Assistant
```

### McpMessage

```text
McpMessage = {
    Role: McpRole
    Content: Content
}
```

### ToolDefinition

```text
ToolDefinition = {
    Name: ToolName
    Description: string
    InputSchema: JsonElement option
    Handler: Map<string, JsonElement> -> Task<Result<Content list, McpError>>
}
```

### ResourceDefinition

```text
ResourceDefinition = {
    Uri: ResourceUri
    Name: string
    Description: string option
    MimeType: MimeType option
    Handler: Map<string, string> -> Task<Result<ResourceContents, McpError>>
}
```

### PromptArgument

```text
PromptArgument = {
    Name: string
    Description: string option
    Required: bool
}
```

### PromptDefinition

```text
PromptDefinition = {
    Name: PromptName
    Description: string option
    Arguments: PromptArgument list
    Handler: Map<string, string> -> Task<Result<McpMessage list, McpError>>
}
```

### McpError

```text
McpError
  = ValidationFailed of ValidationError list
  | ToolNotFound of ToolName
  | ResourceNotFound of ResourceUri
  | PromptNotFound of PromptName
  | HandlerException of exn
  | TransportError of message: string
  | ProtocolError of code: int * message: string
```

## FsMcp.Server — Server Types

### ServerConfig

```text
ServerConfig = {
    Name: ServerName
    Version: ServerVersion
    Tools: ToolDefinition list
    Resources: ResourceDefinition list
    Prompts: PromptDefinition list
    Middleware: McpMiddleware list
}
```

### McpContext

```text
McpContext = {
    Method: string
    Params: JsonElement option
    Services: IServiceProvider
    Logger: ILogger
    CancellationToken: CancellationToken
}
```

### McpResponse

```text
McpResponse
  = Success of JsonElement
  | Error of McpError
```

### McpMiddleware

```text
McpMiddleware =
    McpContext -> (McpContext -> Task<McpResponse>) -> Task<McpResponse>
```

### Transport

```text
Transport
  = Stdio
  | Http of endpoint: string option
```

## FsMcp.Client — Client Types

### ClientConfig

```text
ClientConfig = {
    Transport: ClientTransport
    Name: string
    ShutdownTimeout: TimeSpan option
}
```

### ClientTransport

```text
ClientTransport
  = StdioProcess of command: string * args: string list
  | HttpEndpoint of uri: Uri * headers: Map<string, string>
```

### ToolCallResult

```text
ToolCallResult = Result<Content list, McpError>
```

## Entity Relationships

```text
ServerConfig ──contains──► ToolDefinition (0..*)
             ──contains──► ResourceDefinition (0..*)
             ──contains──► PromptDefinition (0..*)
             ──contains──► McpMiddleware (0..*)

ToolDefinition ──uses──► ToolName (1)
               ──returns──► Content (0..*)

ResourceDefinition ──uses──► ResourceUri (1)
                   ──returns──► ResourceContents (1)

PromptDefinition ──uses──► PromptName (1)
                 ──contains──► PromptArgument (0..*)
                 ──returns──► McpMessage (0..*)

Content ──may contain──► ResourceContents (via EmbeddedResource)

McpMessage ──contains──► Content (1)
           ──contains──► McpRole (1)
```

## Uniqueness Constraints

- `ToolName` MUST be unique within a `ServerConfig`
- `ResourceUri` MUST be unique within a `ServerConfig`
- `PromptName` MUST be unique within a `ServerConfig`
- Enforced at build time (server builder rejects duplicates)

## Validation Rules

| Type | Rule |
|------|------|
| ToolName | Non-empty after trim |
| ResourceUri | Non-empty, valid URI format |
| PromptName | Non-empty after trim |
| MimeType | Empty (default) or valid `type/subtype` format |
| ServerName | Non-empty after trim |
| ServerVersion | Non-empty after trim |
