module FsMcp.Core.Tests.SerializationTests

open System
open System.Text.Json
open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Core.Serialization

// ───────────────────────────────────────────────────────
//  Helpers
// ───────────────────────────────────────────────────────

let private unwrap result =
    match result with
    | Ok v -> v
    | Error e -> failtest $"unexpected error: %A{e}"

// ───────────────────────────────────────────────────────
//  Content.Text serialization
// ───────────────────────────────────────────────────────

let contentTextTests =
    testList "Content.Text serialization" [
        testCase "serializes Text to expected JSON shape" <| fun _ ->
            let content = Text "hello world"
            let json = serialize content
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            Expect.equal (root.GetProperty("type").GetString()) "text" "type field"
            Expect.equal (root.GetProperty("text").GetString()) "hello world" "text field"

        testCase "deserializes JSON to Text" <| fun _ ->
            let json = """{"type":"text","text":"hello world"}"""
            let content = deserialize<Content> json
            match content with
            | Text t -> Expect.equal t "hello world" "text matches"
            | other -> failtest $"expected Text, got %A{other}"

        testCase "Text roundtrip preserves value" <| fun _ ->
            let original = Text "roundtrip test"
            let json = serialize original
            let deserialized = deserialize<Content> json
            match deserialized with
            | Text t -> Expect.equal t "roundtrip test" "roundtrip text"
            | other -> failtest $"expected Text, got %A{other}"

        testCase "Text with empty string roundtrips" <| fun _ ->
            let original = Text ""
            let json = serialize original
            let deserialized = deserialize<Content> json
            match deserialized with
            | Text t -> Expect.equal t "" "empty text"
            | other -> failtest $"expected Text, got %A{other}"

        testCase "Text with special characters roundtrips" <| fun _ ->
            let special = "line1\nline2\ttab \"quoted\" \\backslash"
            let original = Text special
            let json = serialize original
            let deserialized = deserialize<Content> json
            match deserialized with
            | Text t -> Expect.equal t special "special chars preserved"
            | other -> failtest $"expected Text, got %A{other}"
    ]

// ───────────────────────────────────────────────────────
//  Content.Image serialization
// ───────────────────────────────────────────────────────

let contentImageTests =
    testList "Content.Image serialization" [
        testCase "serializes Image with base64 data" <| fun _ ->
            let data = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |]
            let mime = MimeType.create "image/png" |> unwrap
            let content = Image(data, mime)
            let json = serialize content
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            Expect.equal (root.GetProperty("type").GetString()) "image" "type field"
            Expect.equal (root.GetProperty("mimeType").GetString()) "image/png" "mimeType field"
            let b64 = root.GetProperty("data").GetString()
            Expect.equal (Convert.FromBase64String(b64)) data "base64 data decodes correctly"

        testCase "deserializes JSON to Image" <| fun _ ->
            let data = [| 1uy; 2uy; 3uy |]
            let b64 = Convert.ToBase64String(data)
            let json = $"""{{ "type":"image", "data":"{b64}", "mimeType":"image/jpeg" }}"""
            let content = deserialize<Content> json
            match content with
            | Image(d, m) ->
                Expect.equal d data "data matches"
                Expect.equal (MimeType.value m) "image/jpeg" "mime matches"
            | other -> failtest $"expected Image, got %A{other}"

        testCase "Image roundtrip preserves data and MIME type" <| fun _ ->
            let data = [| 0xFFuy; 0xD8uy; 0xFFuy; 0xE0uy |]
            let mime = MimeType.create "image/jpeg" |> unwrap
            let original = Image(data, mime)
            let json = serialize original
            let deserialized = deserialize<Content> json
            match deserialized with
            | Image(d, m) ->
                Expect.equal d data "data roundtrip"
                Expect.equal (MimeType.value m) "image/jpeg" "mime roundtrip"
            | other -> failtest $"expected Image, got %A{other}"

        testCase "Image with empty data roundtrips" <| fun _ ->
            let mime = MimeType.create "image/gif" |> unwrap
            let original = Image([||], mime)
            let json = serialize original
            let deserialized = deserialize<Content> json
            match deserialized with
            | Image(d, _) -> Expect.equal d [||] "empty data"
            | other -> failtest $"expected Image, got %A{other}"
    ]

// ───────────────────────────────────────────────────────
//  Content.EmbeddedResource serialization
// ───────────────────────────────────────────────────────

let contentEmbeddedResourceTests =
    testList "Content.EmbeddedResource serialization" [
        testCase "serializes EmbeddedResource with TextResource" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/doc.txt" |> unwrap
            let mime = MimeType.create "text/plain" |> unwrap
            let content = EmbeddedResource(TextResource(uri, mime, "file contents"))
            let json = serialize content
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            Expect.equal (root.GetProperty("type").GetString()) "resource" "type field"
            let res = root.GetProperty("resource")
            Expect.equal (res.GetProperty("uri").GetString()) "https://example.com/doc.txt" "uri"
            Expect.equal (res.GetProperty("mimeType").GetString()) "text/plain" "mimeType"
            Expect.equal (res.GetProperty("text").GetString()) "file contents" "text"

        testCase "serializes EmbeddedResource with BlobResource" <| fun _ ->
            let uri = ResourceUri.create "file:///tmp/data.bin" |> unwrap
            let mime = MimeType.create "application/octet-stream" |> unwrap
            let data = [| 0xDEuy; 0xADuy |]
            let content = EmbeddedResource(BlobResource(uri, mime, data))
            let json = serialize content
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let res = root.GetProperty("resource")
            Expect.equal (res.GetProperty("blob").GetString()) (Convert.ToBase64String(data)) "blob base64"

        testCase "EmbeddedResource TextResource roundtrip" <| fun _ ->
            let uri = ResourceUri.create "mcp://server/resource" |> unwrap
            let mime = MimeType.create "application/json" |> unwrap
            let original = EmbeddedResource(TextResource(uri, mime, "{\"key\":\"value\"}"))
            let json = serialize original
            let deserialized = deserialize<Content> json
            match deserialized with
            | EmbeddedResource(TextResource(u, m, t)) ->
                Expect.equal (ResourceUri.value u) "mcp://server/resource" "uri roundtrip"
                Expect.equal (MimeType.value m) "application/json" "mime roundtrip"
                Expect.equal t "{\"key\":\"value\"}" "text roundtrip"
            | other -> failtest $"expected EmbeddedResource(TextResource), got %A{other}"

        testCase "EmbeddedResource BlobResource roundtrip" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/bin" |> unwrap
            let mime = MimeType.create "application/octet-stream" |> unwrap
            let data = [| 1uy; 2uy; 3uy; 4uy; 5uy |]
            let original = EmbeddedResource(BlobResource(uri, mime, data))
            let json = serialize original
            let deserialized = deserialize<Content> json
            match deserialized with
            | EmbeddedResource(BlobResource(u, m, d)) ->
                Expect.equal (ResourceUri.value u) "https://example.com/bin" "uri roundtrip"
                Expect.equal d data "data roundtrip"
            | other -> failtest $"expected EmbeddedResource(BlobResource), got %A{other}"
    ]

// ───────────────────────────────────────────────────────
//  ResourceContents serialization
// ───────────────────────────────────────────────────────

let resourceContentsTests =
    testList "ResourceContents serialization" [
        testCase "TextResource serializes to expected JSON" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/readme" |> unwrap
            let mime = MimeType.create "text/markdown" |> unwrap
            let rc = TextResource(uri, mime, "# Hello")
            let json = serialize rc
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            Expect.equal (root.GetProperty("uri").GetString()) "https://example.com/readme" "uri"
            Expect.equal (root.GetProperty("mimeType").GetString()) "text/markdown" "mime"
            Expect.equal (root.GetProperty("text").GetString()) "# Hello" "text"

        testCase "BlobResource serializes to expected JSON" <| fun _ ->
            let uri = ResourceUri.create "file:///data.bin" |> unwrap
            let mime = MimeType.create "application/octet-stream" |> unwrap
            let data = [| 0xCAuy; 0xFEuy |]
            let rc = BlobResource(uri, mime, data)
            let json = serialize rc
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            Expect.equal (root.GetProperty("uri").GetString()) "file:///data.bin" "uri"
            Expect.equal (root.GetProperty("blob").GetString()) (Convert.ToBase64String(data)) "blob"

        testCase "TextResource JSON deserializes correctly" <| fun _ ->
            let json = """{"uri":"https://example.com/x","mimeType":"text/plain","text":"content"}"""
            let rc = deserialize<ResourceContents> json
            match rc with
            | TextResource(u, m, t) ->
                Expect.equal (ResourceUri.value u) "https://example.com/x" "uri"
                Expect.equal (MimeType.value m) "text/plain" "mime"
                Expect.equal t "content" "text"
            | other -> failtest $"expected TextResource, got %A{other}"

        testCase "BlobResource JSON deserializes correctly" <| fun _ ->
            let data = [| 0xABuy; 0xCDuy |]
            let b64 = Convert.ToBase64String(data)
            let json = $"""{{ "uri":"file:///test.bin", "mimeType":"application/octet-stream", "blob":"{b64}" }}"""
            let rc = deserialize<ResourceContents> json
            match rc with
            | BlobResource(_, _, d) -> Expect.equal d data "data"
            | other -> failtest $"expected BlobResource, got %A{other}"

        testCase "TextResource roundtrip" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/doc" |> unwrap
            let mime = MimeType.create "text/html" |> unwrap
            let original = TextResource(uri, mime, "<html></html>")
            let json = serialize original
            let rt = deserialize<ResourceContents> json
            match rt with
            | TextResource(u, m, t) ->
                Expect.equal (ResourceUri.value u) "https://example.com/doc" "uri"
                Expect.equal (MimeType.value m) "text/html" "mime"
                Expect.equal t "<html></html>" "text"
            | other -> failtest $"expected TextResource, got %A{other}"
    ]

// ───────────────────────────────────────────────────────
//  Error cases
// ───────────────────────────────────────────────────────

let errorCaseTests =
    testList "Serialization error cases" [
        testCase "missing 'type' field in Content throws JsonException" <| fun _ ->
            let json = """{"text":"hello"}"""
            Expect.throws
                (fun () -> deserialize<Content> json |> ignore)
                "missing type should throw"

        testCase "unknown Content type throws JsonException" <| fun _ ->
            let json = """{"type":"video","url":"http://x.com"}"""
            Expect.throws
                (fun () -> deserialize<Content> json |> ignore)
                "unknown type should throw"

        testCase "missing 'text' in text Content throws JsonException" <| fun _ ->
            let json = """{"type":"text"}"""
            Expect.throws
                (fun () -> deserialize<Content> json |> ignore)
                "missing text field should throw"

        testCase "missing 'data' in image Content throws JsonException" <| fun _ ->
            let json = """{"type":"image","mimeType":"image/png"}"""
            Expect.throws
                (fun () -> deserialize<Content> json |> ignore)
                "missing data field should throw"

        testCase "missing 'mimeType' in image Content throws JsonException" <| fun _ ->
            let json = """{"type":"image","data":"AQID"}"""
            Expect.throws
                (fun () -> deserialize<Content> json |> ignore)
                "missing mimeType should throw"

        testCase "missing 'resource' in resource Content throws JsonException" <| fun _ ->
            let json = """{"type":"resource"}"""
            Expect.throws
                (fun () -> deserialize<Content> json |> ignore)
                "missing resource should throw"

        testCase "missing 'uri' in ResourceContents throws JsonException" <| fun _ ->
            let json = """{"mimeType":"text/plain","text":"hello"}"""
            Expect.throws
                (fun () -> deserialize<ResourceContents> json |> ignore)
                "missing uri should throw"

        testCase "ResourceContents with neither text nor blob throws" <| fun _ ->
            let json = """{"uri":"https://example.com/x","mimeType":"text/plain"}"""
            Expect.throws
                (fun () -> deserialize<ResourceContents> json |> ignore)
                "neither text nor blob should throw"

        testCase "invalid base64 in blob throws" <| fun _ ->
            let json = """{"uri":"https://example.com/x","mimeType":"application/octet-stream","blob":"not-valid-base64!!!"}"""
            Expect.throws
                (fun () -> deserialize<ResourceContents> json |> ignore)
                "invalid base64 should throw"

        testCase "invalid URI in ResourceContents throws" <| fun _ ->
            let json = """{"uri":"not a uri","mimeType":"text/plain","text":"hello"}"""
            Expect.throws
                (fun () -> deserialize<ResourceContents> json |> ignore)
                "invalid uri should throw"
    ]

[<Tests>]
let allSerializationTests =
    testList "Serialization" [
        contentTextTests
        contentImageTests
        contentEmbeddedResourceTests
        resourceContentsTests
        errorCaseTests
    ]
