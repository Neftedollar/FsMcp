module FsMcp.Testing.Tests.McpArbitrariesTests

open Expecto
open FsCheck
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Testing

let config =
    { FsCheckConfig.defaultConfig with
        maxTest = 100
        arbitrary = [ typeof<McpArbitraries.McpArbitraryProvider> ] }

// ───────────────────────────────────────────────────────
//  Generator validity properties
// ───────────────────────────────────────────────────────

let generatorValidityTests =
    testList "McpArbitraries generator validity" [

        testPropertyWithConfig config "ToolName generator produces valid names"
            <| fun (tn: ToolName) ->
                let v = ToolName.value tn
                not (System.String.IsNullOrWhiteSpace v)

        testPropertyWithConfig config "ResourceUri generator produces valid URIs"
            <| fun (uri: ResourceUri) ->
                let v = ResourceUri.value uri
                v.Contains("://")

        testPropertyWithConfig config "PromptName generator produces valid names"
            <| fun (pn: PromptName) ->
                let v = PromptName.value pn
                not (System.String.IsNullOrWhiteSpace v)

        testPropertyWithConfig config "MimeType generator produces values with slash"
            <| fun (mt: MimeType) ->
                let v = MimeType.value mt
                v.Contains("/")

        testPropertyWithConfig config "Content generator produces non-null values"
            <| fun (c: Content) ->
                match c with
                | Text _ -> true
                | Image (data, _) -> not (isNull data)
                | EmbeddedResource _ -> true

        testPropertyWithConfig config "ResourceContents generator produces valid contents"
            <| fun (rc: ResourceContents) ->
                match rc with
                | TextResource (uri, mime, text) ->
                    (ResourceUri.value uri).Contains("://")
                    && (MimeType.value mime).Contains("/")
                    && not (isNull text)
                | BlobResource (uri, mime, data) ->
                    (ResourceUri.value uri).Contains("://")
                    && (MimeType.value mime).Contains("/")
                    && not (isNull data)

        testPropertyWithConfig config "McpMessage generator produces valid messages"
            <| fun (msg: McpMessage) ->
                match msg.Role with
                | User | Assistant -> true
    ]

// ───────────────────────────────────────────────────────
//  Roundtrip properties
// ───────────────────────────────────────────────────────

let roundtripTests =
    testList "McpArbitraries roundtrip" [

        testPropertyWithConfig config "ToolName roundtrips through create"
            <| fun (tn: ToolName) ->
                let v = ToolName.value tn
                match ToolName.create v with
                | Ok tn2 -> ToolName.value tn2 = v
                | Error _ -> false

        testPropertyWithConfig config "ResourceUri roundtrips through create"
            <| fun (uri: ResourceUri) ->
                let v = ResourceUri.value uri
                match ResourceUri.create v with
                | Ok uri2 -> ResourceUri.value uri2 = v
                | Error _ -> false

        testPropertyWithConfig config "PromptName roundtrips through create"
            <| fun (pn: PromptName) ->
                let v = PromptName.value pn
                match PromptName.create v with
                | Ok pn2 -> PromptName.value pn2 = v
                | Error _ -> false

        testPropertyWithConfig config "MimeType roundtrips through create"
            <| fun (mt: MimeType) ->
                let v = MimeType.value mt
                match MimeType.create v with
                | Ok mt2 -> MimeType.value mt2 = v
                | Error _ -> false
    ]

// ───────────────────────────────────────────────────────
//  toolCallArgs property
// ───────────────────────────────────────────────────────

let toolCallArgsTests =
    testList "McpArbitraries toolCallArgs" [

        testPropertyWithConfig config "toolCallArgs generates valid JsonElement objects"
            <| fun (elem: System.Text.Json.JsonElement) ->
                elem.ValueKind = System.Text.Json.JsonValueKind.Object
    ]

// ───────────────────────────────────────────────────────
//  register function tests
// ───────────────────────────────────────────────────────

let registerTests =
    testList "McpArbitraries.register" [
        testCase "register does not throw" <| fun () ->
            McpArbitraries.register ()
    ]

// ───────────────────────────────────────────────────────
//  Public arbitrary values tests
// ───────────────────────────────────────────────────────

let publicArbitraryTests =
    testList "McpArbitraries public values" [
        testCase "toolName arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.toolName.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one ToolName"

        testCase "resourceUri arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.resourceUri.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one ResourceUri"

        testCase "promptName arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.promptName.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one PromptName"

        testCase "mimeType arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.mimeType.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one MimeType"

        testCase "content arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.content.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one Content"

        testCase "resourceContents arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.resourceContents.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one ResourceContents"

        testCase "mcpMessage arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.mcpMessage.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one McpMessage"

        testCase "toolCallArgs arbitrary generates samples" <| fun () ->
            let samples = Gen.sample 10 5 McpArbitraries.toolCallArgs.Generator
            Expecto.Expect.isGreaterThan (List.length samples) 0 "should generate at least one JsonElement"
    ]

// ───────────────────────────────────────────────────────
//  Test list
// ───────────────────────────────────────────────────────

[<Tests>]
let allArbitraryTests =
    testList "McpArbitraries module" [
        generatorValidityTests
        roundtripTests
        toolCallArgsTests
        registerTests
        publicArbitraryTests
    ]
