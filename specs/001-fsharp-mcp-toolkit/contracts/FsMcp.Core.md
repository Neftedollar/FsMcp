# FsMcp.Core — Public API Contract

## Module: FsMcp.Types

All domain types (DUs, records) as defined in data-model.md.
Types are immutable F# records and discriminated unions.

## Module: FsMcp.Validation

```fsharp
/// Smart constructors for identifier types
val ToolName.create     : string -> Result<ToolName, ValidationError>
val ResourceUri.create  : string -> Result<ResourceUri, ValidationError>
val PromptName.create   : string -> Result<PromptName, ValidationError>
val MimeType.create     : string -> Result<MimeType, ValidationError>
val ServerName.create   : string -> Result<ServerName, ValidationError>
val ServerVersion.create: string -> Result<ServerVersion, ValidationError>

/// Unwrap identifier value
val ToolName.value     : ToolName -> string
val ResourceUri.value  : ResourceUri -> string
val PromptName.value   : PromptName -> string
val MimeType.value     : MimeType -> string
val ServerName.value   : ServerName -> string
val ServerVersion.value: ServerVersion -> string
```

## Module: FsMcp.Content

```fsharp
/// Content constructors (convenience)
val Content.text     : string -> Content
val Content.image    : byte[] -> MimeType -> Content
val Content.resource : ResourceContents -> Content
```

## Module: FsMcp.Serialization

```fsharp
/// JSON serialization with MCP wire-format compatibility
val serialize   : 'T -> string          (where 'T is a domain type)
val deserialize : string -> Result<'T, string>

/// JsonSerializerOptions pre-configured for FsMcp types
val jsonOptions : JsonSerializerOptions
```

## Module: FsMcp.Interop (internal)

```fsharp
/// Internal conversion between F# domain types and C# SDK types
/// Not part of the public API surface (Constitution Principle VI)
val internal toSdkContent            : Content -> ContentBlock
val internal fromSdkContent          : ContentBlock -> Result<Content, string>
val internal toSdkResourceContents   : ResourceContents -> ResourceContents
val internal fromSdkResourceContents : ResourceContents -> Result<ResourceContents, string>
```
