namespace FsMcp.Core

open System
open System.Text.Json
open System.Text.Json.Serialization
open FsMcp.Core.Validation

/// JSON serialization support for FsMcp domain types.
/// Provides custom converters matching the MCP wire format.
module Serialization =

    // ───────────────────────────────────────────────────────
    //  ResourceContents converter
    // ───────────────────────────────────────────────────────

    /// Converts ResourceContents to/from MCP wire format:
    /// TextResource  -> {"uri":"...","mimeType":"...","text":"..."}
    /// BlobResource  -> {"uri":"...","mimeType":"...","blob":"base64..."}
    type ResourceContentsConverter() =
        inherit JsonConverter<ResourceContents>()

        override _.Read(reader, _typeToConvert, options) =
            use doc = JsonDocument.ParseValue(&reader)
            let root = doc.RootElement

            let uri =
                match root.TryGetProperty("uri") with
                | true, v ->
                    match ResourceUri.create (v.GetString()) with
                    | Ok u -> u
                    | Error e -> raise (JsonException($"Invalid uri in ResourceContents: %A{e}"))
                | _ -> raise (JsonException("Missing 'uri' field in ResourceContents"))

            let mimeType =
                match root.TryGetProperty("mimeType") with
                | true, v ->
                    match MimeType.create (v.GetString()) with
                    | Ok m -> m
                    | Error e -> raise (JsonException($"Invalid mimeType in ResourceContents: %A{e}"))
                | _ ->
                    match MimeType.create null with
                    | Ok m -> m
                    | Error e -> raise (JsonException($"Failed to create default MimeType: %A{e}"))

            match root.TryGetProperty("text") with
            | true, textEl ->
                TextResource(uri, mimeType, textEl.GetString())
            | _ ->
                match root.TryGetProperty("blob") with
                | true, blobEl ->
                    let data = Convert.FromBase64String(blobEl.GetString())
                    BlobResource(uri, mimeType, data)
                | _ ->
                    raise (JsonException("ResourceContents must have either 'text' or 'blob' field"))

        override _.Write(writer, value, _options) =
            writer.WriteStartObject()

            match value with
            | TextResource(uri, mimeType, text) ->
                writer.WriteString("uri", ResourceUri.value uri)
                writer.WriteString("mimeType", MimeType.value mimeType)
                writer.WriteString("text", text)
            | BlobResource(uri, mimeType, data) ->
                writer.WriteString("uri", ResourceUri.value uri)
                writer.WriteString("mimeType", MimeType.value mimeType)
                writer.WriteString("blob", Convert.ToBase64String(data))

            writer.WriteEndObject()

    // ───────────────────────────────────────────────────────
    //  Content converter
    // ───────────────────────────────────────────────────────

    /// Converts Content to/from MCP wire format:
    /// Text             -> {"type":"text","text":"..."}
    /// Image            -> {"type":"image","data":"base64...","mimeType":"..."}
    /// EmbeddedResource -> {"type":"resource","resource":{...}}
    type ContentConverter() =
        inherit JsonConverter<Content>()

        override _.Read(reader, _typeToConvert, options) =
            use doc = JsonDocument.ParseValue(&reader)
            let root = doc.RootElement

            let typeStr =
                match root.TryGetProperty("type") with
                | true, v -> v.GetString()
                | _ -> raise (JsonException("Missing 'type' field in Content"))

            match typeStr with
            | "text" ->
                match root.TryGetProperty("text") with
                | true, v -> Text(v.GetString())
                | _ -> raise (JsonException("Missing 'text' field in Content of type 'text'"))
            | "image" ->
                let data =
                    match root.TryGetProperty("data") with
                    | true, v -> Convert.FromBase64String(v.GetString())
                    | _ -> raise (JsonException("Missing 'data' field in Content of type 'image'"))

                let mimeType =
                    match root.TryGetProperty("mimeType") with
                    | true, v ->
                        match MimeType.create (v.GetString()) with
                        | Ok m -> m
                        | Error e -> raise (JsonException($"Invalid mimeType in Content: %A{e}"))
                    | _ -> raise (JsonException("Missing 'mimeType' field in Content of type 'image'"))

                Image(data, mimeType)
            | "resource" ->
                match root.TryGetProperty("resource") with
                | true, resEl ->
                    let resourceJson = resEl.GetRawText()
                    let rcOptions = JsonSerializerOptions()
                    rcOptions.Converters.Add(ResourceContentsConverter())
                    let resource = JsonSerializer.Deserialize<ResourceContents>(resourceJson, rcOptions)
                    EmbeddedResource resource
                | _ -> raise (JsonException("Missing 'resource' field in Content of type 'resource'"))
            | other ->
                raise (JsonException($"Unknown Content type: '{other}'"))

        override _.Write(writer, value, options) =
            writer.WriteStartObject()

            match value with
            | Text text ->
                writer.WriteString("type", "text")
                writer.WriteString("text", text)
            | Image(data, mimeType) ->
                writer.WriteString("type", "image")
                writer.WriteString("data", Convert.ToBase64String(data))
                writer.WriteString("mimeType", MimeType.value mimeType)
            | EmbeddedResource resource ->
                writer.WriteString("type", "resource")
                writer.WritePropertyName("resource")
                let rcOptions = JsonSerializerOptions()
                rcOptions.Converters.Add(ResourceContentsConverter())
                JsonSerializer.Serialize(writer, resource, rcOptions)

            writer.WriteEndObject()

    // ───────────────────────────────────────────────────────
    //  Pre-configured options & helpers
    // ───────────────────────────────────────────────────────

    /// Pre-configured JsonSerializerOptions with all FsMcp converters registered.
    let jsonOptions =
        let opts = JsonSerializerOptions()
        opts.Converters.Add(ContentConverter())
        opts.Converters.Add(ResourceContentsConverter())
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    /// Serialize an F# value to a JSON string using FsMcp converters.
    let serialize<'T> (value: 'T) : string =
        JsonSerializer.Serialize(value, jsonOptions)

    /// Deserialize a JSON string to an F# value using FsMcp converters.
    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, jsonOptions)
