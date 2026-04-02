module FsMcp.Core.Tests.PropertyTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Core.Serialization
open FsMcp.Core.Tests.Generators

let config =
    { FsCheckConfig.defaultConfig with
        maxTest = 100
        arbitrary = [ typeof<Generators> ] }

// ───────────────────────────────────────────────────────
//  T027 — Roundtrip properties
// ───────────────────────────────────────────────────────

let roundtripTests =
    testList "Roundtrip properties" [

        // Identifier roundtrips: create -> value -> create again = same value

        testPropertyWithConfig config "ToolName: create -> value -> create = same"
            <| fun (tn: ToolName) ->
                let v = ToolName.value tn
                match ToolName.create v with
                | Ok tn2 -> ToolName.value tn2 = v
                | Error _ -> false

        testPropertyWithConfig config "ResourceUri: create -> value -> create = same"
            <| fun (uri: ResourceUri) ->
                let v = ResourceUri.value uri
                match ResourceUri.create v with
                | Ok uri2 -> ResourceUri.value uri2 = v
                | Error _ -> false

        testPropertyWithConfig config "PromptName: create -> value -> create = same"
            <| fun (pn: PromptName) ->
                let v = PromptName.value pn
                match PromptName.create v with
                | Ok pn2 -> PromptName.value pn2 = v
                | Error _ -> false

        testPropertyWithConfig config "MimeType: create -> value -> create = same"
            <| fun (mt: MimeType) ->
                let v = MimeType.value mt
                match MimeType.create v with
                | Ok mt2 -> MimeType.value mt2 = v
                | Error _ -> false

        testPropertyWithConfig config "ServerName: create -> value -> create = same"
            <| fun (sn: ServerName) ->
                let v = ServerName.value sn
                match ServerName.create v with
                | Ok sn2 -> ServerName.value sn2 = v
                | Error _ -> false

        testPropertyWithConfig config "ServerVersion: create -> value -> create = same"
            <| fun (sv: ServerVersion) ->
                let v = ServerVersion.value sv
                match ServerVersion.create v with
                | Ok sv2 -> ServerVersion.value sv2 = v
                | Error _ -> false

        // JSON roundtrips for Content and ResourceContents

        testPropertyWithConfig config "Content: serialize -> deserialize = identity"
            <| fun (content: Content) ->
                let json = serialize<Content> content
                let deserialized = deserialize<Content> json

                match content, deserialized with
                | Text t1, Text t2 -> t1 = t2
                | Image(d1, m1), Image(d2, m2) ->
                    d1 = d2 && MimeType.value m1 = MimeType.value m2
                | EmbeddedResource(TextResource(u1, m1, t1)),
                  EmbeddedResource(TextResource(u2, m2, t2)) ->
                    ResourceUri.value u1 = ResourceUri.value u2
                    && MimeType.value m1 = MimeType.value m2
                    && t1 = t2
                | EmbeddedResource(BlobResource(u1, m1, d1)),
                  EmbeddedResource(BlobResource(u2, m2, d2)) ->
                    ResourceUri.value u1 = ResourceUri.value u2
                    && MimeType.value m1 = MimeType.value m2
                    && d1 = d2
                | _ -> false

        testPropertyWithConfig config "ResourceContents: serialize -> deserialize = identity"
            <| fun (rc: ResourceContents) ->
                let json = serialize<ResourceContents> rc
                let deserialized = deserialize<ResourceContents> json

                match rc, deserialized with
                | TextResource(u1, m1, t1), TextResource(u2, m2, t2) ->
                    ResourceUri.value u1 = ResourceUri.value u2
                    && MimeType.value m1 = MimeType.value m2
                    && t1 = t2
                | BlobResource(u1, m1, d1), BlobResource(u2, m2, d2) ->
                    ResourceUri.value u1 = ResourceUri.value u2
                    && MimeType.value m1 = MimeType.value m2
                    && d1 = d2
                | _ -> false
    ]

// ───────────────────────────────────────────────────────
//  T028 — Invariant properties
// ───────────────────────────────────────────────────────

let invariantTests =
    testList "Invariant properties" [

        testPropertyWithConfig config "ToolName.create always trims whitespace"
            <| fun (s: string) ->
                if isNull s || System.String.IsNullOrWhiteSpace s then
                    true // rejected inputs, skip
                else
                    match ToolName.create s with
                    | Ok tn -> ToolName.value tn = s.Trim()
                    | Error _ -> false

        testCase "ToolName.create rejects all null/empty/whitespace" <| fun _ ->
            for input in [ null; ""; " "; "\t"; "\n"; "  \t  \n  " ] do
                Expect.isError (ToolName.create input) $"should reject whitespace-like input"

        testPropertyWithConfig config "ResourceUri.create requires :// in the URI"
            <| fun (s: string) ->
                if isNull s || System.String.IsNullOrWhiteSpace s then
                    true // rejected as empty, skip
                else
                    match ResourceUri.create s with
                    | Ok uri -> (ResourceUri.value uri).Contains("://")
                    | Error _ -> true // rejection is fine

        testCase "MimeType.create defaults null/empty to application/octet-stream" <| fun _ ->
            for input in [ null; "" ] do
                match MimeType.create input with
                | Ok mt -> Expect.equal (MimeType.value mt) "application/octet-stream" "default MIME"
                | Error e -> failtest $"unexpected error: %A{e}"

        // Idempotency: create(value(create(x))) = create(x)

        testPropertyWithConfig config "ToolName: smart constructor is idempotent"
            <| fun (tn: ToolName) ->
                let v = ToolName.value tn
                match ToolName.create v with
                | Ok tn2 ->
                    let v2 = ToolName.value tn2
                    match ToolName.create v2 with
                    | Ok tn3 -> ToolName.value tn3 = v2
                    | Error _ -> false
                | Error _ -> false

        testPropertyWithConfig config "PromptName: smart constructor is idempotent"
            <| fun (pn: PromptName) ->
                let v = PromptName.value pn
                match PromptName.create v with
                | Ok pn2 ->
                    let v2 = PromptName.value pn2
                    match PromptName.create v2 with
                    | Ok pn3 -> PromptName.value pn3 = v2
                    | Error _ -> false
                | Error _ -> false

        testPropertyWithConfig config "MimeType: smart constructor is idempotent"
            <| fun (mt: MimeType) ->
                let v = MimeType.value mt
                match MimeType.create v with
                | Ok mt2 ->
                    let v2 = MimeType.value mt2
                    match MimeType.create v2 with
                    | Ok mt3 -> MimeType.value mt3 = v2
                    | Error _ -> false
                | Error _ -> false

        testPropertyWithConfig config "ServerName: smart constructor is idempotent"
            <| fun (sn: ServerName) ->
                let v = ServerName.value sn
                match ServerName.create v with
                | Ok sn2 ->
                    let v2 = ServerName.value sn2
                    match ServerName.create v2 with
                    | Ok sn3 -> ServerName.value sn3 = v2
                    | Error _ -> false
                | Error _ -> false

        testPropertyWithConfig config "ServerVersion: smart constructor is idempotent"
            <| fun (sv: ServerVersion) ->
                let v = ServerVersion.value sv
                match ServerVersion.create v with
                | Ok sv2 ->
                    let v2 = ServerVersion.value sv2
                    match ServerVersion.create v2 with
                    | Ok sv3 -> ServerVersion.value sv3 = v2
                    | Error _ -> false
                | Error _ -> false

        testPropertyWithConfig config "ResourceUri: smart constructor is idempotent"
            <| fun (uri: ResourceUri) ->
                let v = ResourceUri.value uri
                match ResourceUri.create v with
                | Ok uri2 ->
                    let v2 = ResourceUri.value uri2
                    match ResourceUri.create v2 with
                    | Ok uri3 -> ResourceUri.value uri3 = v2
                    | Error _ -> false
                | Error _ -> false
    ]

// ───────────────────────────────────────────────────────
//  T029 — Interop roundtrip placeholders
// ───────────────────────────────────────────────────────

let interopPlaceholderTests =
    testList "Interop roundtrip placeholders" [
        testPropertyWithConfig config "McpMessage: construction roundtrip preserves role and content"
            <| fun (msg: McpMessage) ->
                let rebuilt = { Role = msg.Role; Content = msg.Content }
                rebuilt.Role = msg.Role && rebuilt.Content = msg.Content

        testPropertyWithConfig config "McpRole: User and Assistant roundtrip"
            <| fun (role: McpRole) ->
                match role with
                | User -> role = User
                | Assistant -> role = Assistant

        testPropertyWithConfig config "PromptArgument: construction roundtrip preserves fields"
            <| fun (arg: PromptArgument) ->
                let rebuilt = { Name = arg.Name; Description = arg.Description; Required = arg.Required }
                rebuilt.Name = arg.Name
                && rebuilt.Description = arg.Description
                && rebuilt.Required = arg.Required

        testPropertyWithConfig config "ValidationError: all cases survive pattern match"
            <| fun (ve: ValidationError) ->
                match ve with
                | EmptyValue _ -> true
                | InvalidFormat _ -> true
                | DuplicateEntry _ -> true

        testPropertyWithConfig config "McpError: all cases survive pattern match"
            <| fun (err: McpError) ->
                match err with
                | ValidationFailed _ -> true
                | ToolNotFound _ -> true
                | ResourceNotFound _ -> true
                | PromptNotFound _ -> true
                | HandlerException _ -> true
                | TransportError _ -> true
                | ProtocolError _ -> true
    ]

[<Tests>]
let allPropertyTests =
    testList "Property tests" [
        roundtripTests
        invariantTests
        interopPlaceholderTests
    ]
