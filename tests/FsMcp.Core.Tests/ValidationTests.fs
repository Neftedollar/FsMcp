module FsMcp.Core.Tests.ValidationTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation

// ───────────────────────────────────────────────────────────
//  ToolName
// ───────────────────────────────────────────────────────────

let toolNameTests =
    testList "ToolName" [
        testCase "creates from valid non-empty string" <| fun _ ->
            let result = ToolName.create "myTool"
            Expect.isOk result "should succeed"
            Expect.equal (result |> Result.map ToolName.value) (Ok "myTool") "value matches"

        testCase "creates from single character" <| fun _ ->
            Expect.isOk (ToolName.create "a") "single char is valid"

        testCase "trims whitespace from value" <| fun _ ->
            let result = ToolName.create "  hello  "
            Expect.equal (result |> Result.map ToolName.value) (Ok "hello") "trimmed"

        testCase "returns EmptyValue error for empty string" <| fun _ ->
            match ToolName.create "" with
            | Error (EmptyValue f) -> Expect.equal f "ToolName" "field name"
            | other -> failtest $"unexpected: %A{other}"

        testCase "returns EmptyValue error for whitespace-only string" <| fun _ ->
            Expect.isError (ToolName.create "   ") "whitespace-only"

        testCase "returns EmptyValue error for null" <| fun _ ->
            Expect.isError (ToolName.create null) "null"

        testCase "preserves internal spaces" <| fun _ ->
            Expect.equal (ToolName.create "my tool" |> Result.map ToolName.value) (Ok "my tool") "internal spaces"

        testCase "handles unicode characters" <| fun _ ->
            Expect.isOk (ToolName.create "werkzeug_äöü") "unicode is valid"
    ]

// ───────────────────────────────────────────────────────────
//  ResourceUri
// ───────────────────────────────────────────────────────────

let resourceUriTests =
    testList "ResourceUri" [
        testCase "creates from valid absolute URI" <| fun _ ->
            let result = ResourceUri.create "https://example.com/resource"
            Expect.isOk result "should succeed"
            Expect.equal (result |> Result.map ResourceUri.value) (Ok "https://example.com/resource") "value"

        testCase "creates from file URI" <| fun _ ->
            Expect.isOk (ResourceUri.create "file:///tmp/data.txt") "file URI"

        testCase "creates from custom scheme URI" <| fun _ ->
            Expect.isOk (ResourceUri.create "mcp://server/resource") "custom scheme"

        testCase "returns EmptyValue error for empty string" <| fun _ ->
            match ResourceUri.create "" with
            | Error (EmptyValue _) -> ()
            | other -> failtest $"unexpected: %A{other}"

        testCase "returns EmptyValue error for null" <| fun _ ->
            Expect.isError (ResourceUri.create null) "null"

        testCase "returns InvalidFormat for non-URI string" <| fun _ ->
            match ResourceUri.create "not a uri" with
            | Error (InvalidFormat ("ResourceUri", "not a uri", _)) -> ()
            | other -> failtest $"unexpected: %A{other}"

        testCase "returns InvalidFormat for relative path without scheme" <| fun _ ->
            Expect.isError (ResourceUri.create "/just/a/path") "relative path"

        testCase "creates from URI with query and fragment" <| fun _ ->
            Expect.isOk (ResourceUri.create "https://example.com/res?q=1#frag") "query+fragment"
    ]

// ───────────────────────────────────────────────────────────
//  PromptName
// ───────────────────────────────────────────────────────────

let promptNameTests =
    testList "PromptName" [
        testCase "creates from valid non-empty string" <| fun _ ->
            Expect.equal (PromptName.create "summarize" |> Result.map PromptName.value) (Ok "summarize") "value"

        testCase "trims whitespace" <| fun _ ->
            Expect.equal (PromptName.create "  analyze  " |> Result.map PromptName.value) (Ok "analyze") "trimmed"

        testCase "returns EmptyValue error for empty string" <| fun _ ->
            match PromptName.create "" with
            | Error (EmptyValue f) -> Expect.equal f "PromptName" "field name"
            | other -> failtest $"unexpected: %A{other}"

        testCase "returns EmptyValue error for whitespace-only" <| fun _ ->
            Expect.isError (PromptName.create "\t\n  ") "whitespace-only"

        testCase "returns EmptyValue error for null" <| fun _ ->
            Expect.isError (PromptName.create null) "null"
    ]

// ───────────────────────────────────────────────────────────
//  MimeType
// ───────────────────────────────────────────────────────────

let mimeTypeTests =
    testList "MimeType" [
        testCase "creates from valid MIME type" <| fun _ ->
            Expect.equal (MimeType.create "text/plain" |> Result.map MimeType.value) (Ok "text/plain") "value"

        testCase "creates from application/json" <| fun _ ->
            Expect.isOk (MimeType.create "application/json") "application/json"

        testCase "creates from MIME with parameters" <| fun _ ->
            Expect.isOk (MimeType.create "text/html; charset=utf-8") "MIME with params"

        testCase "creates default for empty string" <| fun _ ->
            Expect.equal (MimeType.create "" |> Result.map MimeType.value) (Ok "application/octet-stream") "default"

        testCase "creates default for null" <| fun _ ->
            Expect.equal (MimeType.create null |> Result.map MimeType.value) (Ok "application/octet-stream") "default"

        testCase "returns InvalidFormat for value without slash" <| fun _ ->
            match MimeType.create "plaintext" with
            | Error (InvalidFormat ("MimeType", "plaintext", _)) -> ()
            | other -> failtest $"unexpected: %A{other}"

        testCase "returns InvalidFormat for bare slash" <| fun _ ->
            Expect.isError (MimeType.create "/") "bare slash"

        testCase "handles vendor MIME types" <| fun _ ->
            Expect.isOk (MimeType.create "application/vnd.api+json") "vendor MIME"
    ]

// ───────────────────────────────────────────────────────────
//  ServerName
// ───────────────────────────────────────────────────────────

let serverNameTests =
    testList "ServerName" [
        testCase "creates from valid non-empty string" <| fun _ ->
            Expect.equal (ServerName.create "my-server" |> Result.map ServerName.value) (Ok "my-server") "value"

        testCase "returns EmptyValue error for empty string" <| fun _ ->
            match ServerName.create "" with
            | Error (EmptyValue f) -> Expect.equal f "ServerName" "field name"
            | other -> failtest $"unexpected: %A{other}"

        testCase "returns EmptyValue error for null" <| fun _ ->
            Expect.isError (ServerName.create null) "null"

        testCase "returns EmptyValue error for whitespace-only" <| fun _ ->
            Expect.isError (ServerName.create "   ") "whitespace-only"

        testCase "preserves value including internal whitespace" <| fun _ ->
            Expect.equal (ServerName.create "My Server v2" |> Result.map ServerName.value) (Ok "My Server v2") "preserved"
    ]

// ───────────────────────────────────────────────────────────
//  ServerVersion
// ───────────────────────────────────────────────────────────

let serverVersionTests =
    testList "ServerVersion" [
        testCase "creates from valid semver string" <| fun _ ->
            Expect.equal (ServerVersion.create "1.0.0" |> Result.map ServerVersion.value) (Ok "1.0.0") "value"

        testCase "creates from any non-empty string" <| fun _ ->
            Expect.isOk (ServerVersion.create "v2-beta") "any non-empty"

        testCase "returns EmptyValue error for empty string" <| fun _ ->
            match ServerVersion.create "" with
            | Error (EmptyValue f) -> Expect.equal f "ServerVersion" "field name"
            | other -> failtest $"unexpected: %A{other}"

        testCase "returns EmptyValue error for null" <| fun _ ->
            Expect.isError (ServerVersion.create null) "null"

        testCase "returns EmptyValue error for whitespace-only" <| fun _ ->
            Expect.isError (ServerVersion.create "  ") "whitespace-only"
    ]

// ───────────────────────────────────────────────────────────
//  FsCheck property-based tests
// ───────────────────────────────────────────────────────────

open FsCheck

let fsCheckConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

let propertyTests =
    testList "Validation property tests" [
        testPropertyWithConfig fsCheckConfig "ToolName roundtrip: create then extract preserves trimmed value"
            <| fun (NonEmptyString s) ->
                if System.String.IsNullOrWhiteSpace s then true
                else
                    match ToolName.create s with
                    | Ok tn -> ToolName.value tn = s.Trim()
                    | Error _ -> false

        testPropertyWithConfig fsCheckConfig "PromptName roundtrip: create then extract preserves trimmed value"
            <| fun (NonEmptyString s) ->
                if System.String.IsNullOrWhiteSpace s then true
                else
                    match PromptName.create s with
                    | Ok pn -> PromptName.value pn = s.Trim()
                    | Error _ -> false

        testCase "ToolName rejects all null/empty/whitespace inputs" <| fun _ ->
            for input in [ null; ""; " "; "\t"; "\n"; "  \t  " ] do
                let label = input |> Option.ofObj |> Option.defaultValue "null"
                Expect.isError (ToolName.create input) $"should reject: {label}"

        testCase "MimeType defaults for null or empty, validates format otherwise" <| fun _ ->
            Expect.isOk (MimeType.create null) "null defaults"
            Expect.isOk (MimeType.create "") "empty defaults"
            Expect.isOk (MimeType.create "text/plain") "text/plain"
            Expect.isError (MimeType.create "noslash") "no slash"

        testPropertyWithConfig fsCheckConfig "ServerName idempotency: creating from already-valid value yields same result"
            <| fun (NonEmptyString s) ->
                if System.String.IsNullOrWhiteSpace s then true
                else
                    match ServerName.create s with
                    | Ok sn ->
                        let v = ServerName.value sn
                        match ServerName.create v with
                        | Ok sn2 -> ServerName.value sn2 = v
                        | Error _ -> false
                    | Error _ -> true

        testPropertyWithConfig fsCheckConfig "ServerVersion idempotency: creating from already-valid value yields same result"
            <| fun (NonEmptyString s) ->
                if System.String.IsNullOrWhiteSpace s then true
                else
                    match ServerVersion.create s with
                    | Ok sv ->
                        let v = ServerVersion.value sv
                        match ServerVersion.create v with
                        | Ok sv2 -> ServerVersion.value sv2 = v
                        | Error _ -> false
                    | Error _ -> true
    ]

[<Tests>]
let allValidationTests =
    testList "Validation" [
        toolNameTests
        resourceUriTests
        promptNameTests
        mimeTypeTests
        serverNameTests
        serverVersionTests
        propertyTests
    ]
