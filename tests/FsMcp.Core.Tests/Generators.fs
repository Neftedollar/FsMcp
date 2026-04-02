module FsMcp.Core.Tests.Generators

open FsCheck
open FsMcp.Core
open FsMcp.Core.Validation

// ───────────────────────────────────────────────────────
//  Helpers
// ───────────────────────────────────────────────────────

/// Generate a non-empty trimmed string suitable for identifier types.
let private nonEmptyTrimmedStringGen =
    Arb.generate<NonEmptyString>
    |> Gen.map (fun (NonEmptyString s) -> s.Trim())
    |> Gen.filter (fun s -> not (System.String.IsNullOrWhiteSpace s))

/// Generate a valid URI string with a random scheme.
let private validUriGen =
    gen {
        let! scheme = Gen.elements [ "https"; "http"; "file"; "mcp"; "ftp" ]
        let! host = Gen.elements [ "example.com"; "localhost"; "server"; "data.io" ]
        let! path = Gen.elements [ "/resource"; "/doc.txt"; "/api/v1"; "/data"; "" ]
        match scheme with
        | "file" -> return $"file:///{host}{path}"
        | _ -> return $"{scheme}://{host}{path}"
    }

/// Generate a valid MIME type string.
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

// ───────────────────────────────────────────────────────
//  Identifier type generators (using smart constructors)
// ───────────────────────────────────────────────────────

let private resultValue r =
    match r with
    | Ok v -> v
    | Error _ -> failwith "unexpected Error in generator after filter"

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

let private serverNameGen =
    nonEmptyTrimmedStringGen
    |> Gen.map ServerName.create
    |> Gen.filter Result.isOk
    |> Gen.map resultValue

let private serverVersionGen =
    gen {
        let! major = Gen.choose (0, 99)
        let! minor = Gen.choose (0, 99)
        let! patch = Gen.choose (0, 99)
        let s = $"{major}.{minor}.{patch}"
        // ServerVersion.create only fails for null/empty/whitespace, so this always succeeds
        return ServerVersion.create s |> Result.defaultWith (fun _ -> failwith "unexpected")
    }

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

let private validationErrorGen =
    gen {
        let! case = Gen.choose (0, 2)
        let! field = nonEmptyTrimmedStringGen

        match case with
        | 0 -> return EmptyValue field
        | 1 ->
            let! value = nonEmptyTrimmedStringGen
            let! expected = nonEmptyTrimmedStringGen
            return InvalidFormat(field, value, expected)
        | _ ->
            let! name = nonEmptyTrimmedStringGen
            return DuplicateEntry(field, name)
    }

let private mcpErrorGen =
    gen {
        let! case = Gen.choose (0, 5)

        match case with
        | 0 ->
            let! errors = Gen.listOfLength 1 validationErrorGen
            return ValidationFailed errors
        | 1 ->
            let! tn = toolNameGen
            return ToolNotFound tn
        | 2 ->
            let! uri = resourceUriGen
            return ResourceNotFound uri
        | 3 ->
            let! pn = promptNameGen
            return PromptNotFound pn
        | 4 ->
            let! msg = nonEmptyTrimmedStringGen
            return TransportError msg
        | _ ->
            let! code = Gen.choose (100, 599)
            let! msg = nonEmptyTrimmedStringGen
            return ProtocolError(code, msg)
    }

let private promptArgumentGen =
    gen {
        let! name = nonEmptyTrimmedStringGen
        let! desc =
            Gen.oneof [
                Gen.constant None
                nonEmptyTrimmedStringGen |> Gen.map Some
            ]
        let! required = Arb.generate<bool>
        return { Name = name; Description = desc; Required = required }
    }

// ───────────────────────────────────────────────────────
//  Arbitrary registrations
// ───────────────────────────────────────────────────────

/// FsCheck Arbitrary generators for all FsMcp domain types.
/// Register via FsCheckConfig: { FsCheckConfig.defaultConfig with arbitrary = [typeof<Generators>] }
type Generators() =
    static member ToolName() : Arbitrary<ToolName> =
        toolNameGen |> Arb.fromGen

    static member ResourceUri() : Arbitrary<ResourceUri> =
        resourceUriGen |> Arb.fromGen

    static member PromptName() : Arbitrary<PromptName> =
        promptNameGen |> Arb.fromGen

    static member MimeType() : Arbitrary<MimeType> =
        mimeTypeGen |> Arb.fromGen

    static member ServerName() : Arbitrary<ServerName> =
        serverNameGen |> Arb.fromGen

    static member ServerVersion() : Arbitrary<ServerVersion> =
        serverVersionGen |> Arb.fromGen

    static member Content() : Arbitrary<Content> =
        contentGen |> Arb.fromGen

    static member ResourceContents() : Arbitrary<ResourceContents> =
        resourceContentsGen |> Arb.fromGen

    static member McpRole() : Arbitrary<McpRole> =
        mcpRoleGen |> Arb.fromGen

    static member McpMessage() : Arbitrary<McpMessage> =
        mcpMessageGen |> Arb.fromGen

    static member McpError() : Arbitrary<McpError> =
        mcpErrorGen |> Arb.fromGen

    static member ValidationError() : Arbitrary<ValidationError> =
        validationErrorGen |> Arb.fromGen

    static member PromptArgument() : Arbitrary<PromptArgument> =
        promptArgumentGen |> Arb.fromGen
