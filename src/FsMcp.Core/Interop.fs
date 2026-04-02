namespace FsMcp.Core

open System.Text.Json
open FsMcp.Core.Validation

/// Internal conversions between F# domain types and C# SDK types.
/// Not part of the public API (Constitution Principle VI).
module internal Interop =

    open ModelContextProtocol.Protocol

    /// Convert F# Content to SDK ContentBlock.
    let toSdkContentBlock (content: Content) : ContentBlock =
        match content with
        | Text text ->
            TextContentBlock(Text = text) :> ContentBlock
        | Image (data, mimeType) ->
            ImageContentBlock(
                MimeType = MimeType.value mimeType,
                Data = System.ReadOnlyMemory(data)) :> ContentBlock
        | EmbeddedResource resource ->
            let sdkResource =
                match resource with
                | TextResource (uri, mimeType, text) ->
                    TextResourceContents(
                        Uri = ResourceUri.value uri,
                        MimeType = MimeType.value mimeType,
                        Text = text) :> ResourceContents
                | BlobResource (uri, mimeType, data) ->
                    BlobResourceContents(
                        Uri = ResourceUri.value uri,
                        MimeType = MimeType.value mimeType,
                        Blob = System.ReadOnlyMemory(data)) :> ResourceContents
            EmbeddedResourceBlock(Resource = sdkResource) :> ContentBlock

    /// Convert SDK ContentBlock to F# Content.
    let fromSdkContentBlock (block: ContentBlock) : Result<Content, string> =
        match block with
        | :? TextContentBlock as t ->
            Ok (Text t.Text)
        | :? ImageContentBlock as img ->
            let data = img.Data.ToArray()
            match MimeType.create (if isNull img.MimeType then "" else img.MimeType) with
            | Ok m -> Ok (Image (data, m))
            | Error _ -> Ok (Text "[image with invalid mime type]")
        | :? EmbeddedResourceBlock as res ->
            match res.Resource with
            | :? TextResourceContents as trc ->
                match ResourceUri.create (if isNull trc.Uri then "" else trc.Uri),
                      MimeType.create (if isNull trc.MimeType then "" else trc.MimeType) with
                | Ok u, Ok m -> Ok (EmbeddedResource (TextResource (u, m, trc.Text)))
                | _ -> Ok (Text (if isNull trc.Text then "" else trc.Text))
            | :? BlobResourceContents as brc ->
                match ResourceUri.create (if isNull brc.Uri then "" else brc.Uri),
                      MimeType.create (if isNull brc.MimeType then "" else brc.MimeType) with
                | Ok u, Ok m -> Ok (EmbeddedResource (BlobResource (u, m, brc.Blob.ToArray())))
                | _ -> Ok (Text "[binary resource with invalid uri/mime]")
            | _ -> Ok (Text "[unknown resource type]")
        | _ -> Ok (Text "[unsupported content type]")

    /// Convert F# McpRole to SDK Role.
    let toSdkRole (role: McpRole) : Role =
        match role with
        | User -> Role.User
        | Assistant -> Role.Assistant

    /// Convert SDK Role to F# McpRole.
    let fromSdkRole (role: Role) : McpRole =
        match role with
        | Role.User -> User
        | Role.Assistant -> Assistant
        | _ -> User
