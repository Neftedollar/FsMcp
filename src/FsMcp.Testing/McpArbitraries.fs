namespace FsMcp.Testing

open FsCheck
open FsMcp.Core
open FsMcp.Core.Validation

/// FsCheck generators for MCP domain types, available to library consumers.
module McpArbitraries =

    // ───────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────

    let private nonEmptyTrimmedStringGen =
        Arb.generate<NonEmptyString>
        |> Gen.map (fun (NonEmptyString s) -> s.Trim())
        |> Gen.filter (fun s -> not (System.String.IsNullOrWhiteSpace s))

    let private validUriGen =
        gen {
            let! scheme = Gen.elements [ "https"; "http"; "file"; "mcp"; "ftp" ]
            let! host = Gen.elements [ "example.com"; "localhost"; "server"; "data.io" ]
            let! path = Gen.elements [ "/resource"; "/doc.txt"; "/api/v1"; "/data"; "" ]
            match scheme with
            | "file" -> return $"file:///{host}{path}"
            | _ -> return $"{scheme}://{host}{path}"
        }

    let private validMimeTypeGen =
        Gen.elements [
            "text/plain"
            "text/html"
            "text/markdown"
            "application/json"
            "application/xml"
            "application/octet-stream"
            "application/vnd.api+json"
            "image/png"
            "image/jpeg"
            "image/gif"
            "image/svg+xml"
            "audio/mpeg"
            "video/mp4"
        ]

    let private resultValue r =
        match r with
        | Ok v -> v
        | Error _ -> failwith "unexpected Error in generator after filter"

    // ───────────────────────────────────────────────────────
    //  Identifier type generators
    // ───────────────────────────────────────────────────────

    let private toolNameGen =
        nonEmptyTrimmedStringGen
        |> Gen.map ToolName.create
        |> Gen.filter Result.isOk
        |> Gen.map resultValue

    let private resourceUriGen =
        validUriGen
        |> Gen.map ResourceUri.create
        |> Gen.filter Result.isOk
        |> Gen.map resultValue

    let private promptNameGen =
        nonEmptyTrimmedStringGen
        |> Gen.map PromptName.create
        |> Gen.filter Result.isOk
        |> Gen.map resultValue

    let private mimeTypeGen =
        validMimeTypeGen
        |> Gen.map MimeType.create
        |> Gen.filter Result.isOk
        |> Gen.map resultValue

    // ───────────────────────────────────────────────────────
    //  Complex type generators
    // ───────────────────────────────────────────────────────

    let private mcpRoleGen =
        Gen.elements [ User; Assistant ]

    let private resourceContentsGen =
        gen {
            let! uri = resourceUriGen
            let! mime = mimeTypeGen
            let! isText = Arb.generate<bool>

            if isText then
                let! text = Arb.generate<NonEmptyString> |> Gen.map (fun (NonEmptyString s) -> s)
                return TextResource(uri, mime, text)
            else
                let! data = Gen.arrayOf (Gen.choose (0, 255) |> Gen.map byte)
                return BlobResource(uri, mime, data)
        }

    let private contentGen =
        gen {
            let! case = Gen.choose (0, 2)

            match case with
            | 0 ->
                let! text = Arb.generate<string> |> Gen.filter (not << isNull)
                return Text text
            | 1 ->
                let! data = Gen.arrayOf (Gen.choose (0, 255) |> Gen.map byte)
                let! mime = mimeTypeGen
                return Image(data, mime)
            | _ ->
                let! resource = resourceContentsGen
                return EmbeddedResource resource
        }

    let private mcpMessageGen =
        gen {
            let! role = mcpRoleGen
            let! content = contentGen
            return { Role = role; Content = content }
        }

    let private toolCallArgsGen =
        gen {
            let! key = nonEmptyTrimmedStringGen
            let! value = Arb.generate<string> |> Gen.filter (not << isNull)
            let json = System.Text.Json.JsonSerializer.Serialize(dict [ key, value ])
            return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone()
        }

    // ───────────────────────────────────────────────────────
    //  Public Arbitrary values
    // ───────────────────────────────────────────────────────

    /// Arbitrary for valid ToolName values.
    let toolName : Arbitrary<ToolName> = toolNameGen |> Arb.fromGen

    /// Arbitrary for valid ResourceUri values.
    let resourceUri : Arbitrary<ResourceUri> = resourceUriGen |> Arb.fromGen

    /// Arbitrary for valid PromptName values.
    let promptName : Arbitrary<PromptName> = promptNameGen |> Arb.fromGen

    /// Arbitrary for valid MimeType values.
    let mimeType : Arbitrary<MimeType> = mimeTypeGen |> Arb.fromGen

    /// Arbitrary for Content values (Text, Image, or EmbeddedResource).
    let content : Arbitrary<Content> = contentGen |> Arb.fromGen

    /// Arbitrary for ResourceContents values (TextResource or BlobResource).
    let resourceContents : Arbitrary<ResourceContents> = resourceContentsGen |> Arb.fromGen

    /// Arbitrary for McpMessage values.
    let mcpMessage : Arbitrary<McpMessage> = mcpMessageGen |> Arb.fromGen

    /// Arbitrary for JSON tool call arguments.
    let toolCallArgs : Arbitrary<System.Text.Json.JsonElement> = toolCallArgsGen |> Arb.fromGen

    // ───────────────────────────────────────────────────────
    //  Arbitrary class for FsCheck registration
    // ───────────────────────────────────────────────────────

    /// FsCheck Arbitrary type class for automatic registration.
    /// Use with: { FsCheckConfig.defaultConfig with arbitrary = [typeof<McpArbitraryProvider>] }
    type McpArbitraryProvider() =
        static member ToolName() : Arbitrary<ToolName> = toolName
        static member ResourceUri() : Arbitrary<ResourceUri> = resourceUri
        static member PromptName() : Arbitrary<PromptName> = promptName
        static member MimeType() : Arbitrary<MimeType> = mimeType
        static member Content() : Arbitrary<Content> = content
        static member ResourceContents() : Arbitrary<ResourceContents> = resourceContents
        static member McpMessage() : Arbitrary<McpMessage> = mcpMessage
        static member ToolCallArgs() : Arbitrary<System.Text.Json.JsonElement> = toolCallArgs

    /// Register all MCP Arbitrary instances with FsCheck globally.
    let register () : unit =
        Arb.register<McpArbitraryProvider>() |> ignore
