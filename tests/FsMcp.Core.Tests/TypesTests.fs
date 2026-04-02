module FsMcp.Core.Tests.TypesTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation

// ───────────────────────────────────────────────────────────
//  Content construction helpers
// ───────────────────────────────────────────────────────────

let contentConstructionTests =
    testList "Content construction" [
        testCase "Content.text creates Text content from string" <| fun _ ->
            match Content.text "hello world" with
            | Text t -> Expect.equal t "hello world" "text matches"
            | other -> failtest $"expected Text, got %A{other}"

        testCase "Content.text creates Text with empty string" <| fun _ ->
            match Content.text "" with
            | Text t -> Expect.equal t "" "empty text"
            | other -> failtest $"expected Text, got %A{other}"

        testCase "Content.text creates Text with multiline string" <| fun _ ->
            let multiline = "line 1\nline 2\nline 3"
            match Content.text multiline with
            | Text t -> Expect.equal t multiline "multiline preserved"
            | other -> failtest $"expected Text, got %A{other}"

        testCase "Content.image creates Image content from bytes and MIME type" <| fun _ ->
            let data = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |]
            let mimeType = MimeType.create "image/png" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            match Content.image data mimeType with
            | Image (d, m) ->
                Expect.equal d data "data matches"
                Expect.equal (MimeType.value m) "image/png" "mime matches"
            | other -> failtest $"expected Image, got %A{other}"

        testCase "Content.image handles empty byte array" <| fun _ ->
            let mimeType = MimeType.create "image/jpeg" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            match Content.image [||] mimeType with
            | Image (d, _) -> Expect.equal d [||] "empty data"
            | other -> failtest $"expected Image, got %A{other}"

        testCase "Content.embeddedResource creates EmbeddedResource from TextResource" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/doc.txt" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let mimeType = MimeType.create "text/plain" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let resource = TextResource (uri, mimeType, "file contents")
            match Content.embeddedResource resource with
            | EmbeddedResource (TextResource (u, m, t)) ->
                Expect.equal (ResourceUri.value u) "https://example.com/doc.txt" "uri"
                Expect.equal (MimeType.value m) "text/plain" "mime"
                Expect.equal t "file contents" "text"
            | other -> failtest $"expected EmbeddedResource(TextResource), got %A{other}"

        testCase "Content.embeddedResource creates EmbeddedResource from BlobResource" <| fun _ ->
            let uri = ResourceUri.create "file:///tmp/image.bin" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let mimeType = MimeType.create "application/octet-stream" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let data = [| 1uy; 2uy; 3uy |]
            match Content.embeddedResource (BlobResource (uri, mimeType, data)) with
            | EmbeddedResource (BlobResource (_, _, d)) -> Expect.equal d data "data matches"
            | other -> failtest $"expected EmbeddedResource(BlobResource), got %A{other}"
    ]

// ───────────────────────────────────────────────────────────
//  ResourceContents
// ───────────────────────────────────────────────────────────

let resourceContentsTests =
    testList "ResourceContents" [
        testCase "TextResource holds URI, MIME type, and text" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/readme" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let mime = MimeType.create "text/markdown" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            match TextResource (uri, mime, "# Hello") with
            | TextResource (u, m, t) ->
                Expect.equal (ResourceUri.value u) "https://example.com/readme" "uri"
                Expect.equal (MimeType.value m) "text/markdown" "mime"
                Expect.equal t "# Hello" "text"
            | _ -> failtest "expected TextResource"

        testCase "BlobResource holds URI, MIME type, and binary data" <| fun _ ->
            let uri = ResourceUri.create "file:///data.bin" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let mime = MimeType.create "application/octet-stream" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            match BlobResource (uri, mime, [| 0xFFuy; 0xD8uy |]) with
            | BlobResource (_, _, d) -> Expect.equal d [| 0xFFuy; 0xD8uy |] "binary data"
            | _ -> failtest "expected BlobResource"
    ]

// ───────────────────────────────────────────────────────────
//  McpRole & McpMessage
// ───────────────────────────────────────────────────────────

let mcpRoleTests =
    testList "McpRole" [
        testCase "User and Assistant are distinct values" <| fun _ ->
            Expect.notEqual User Assistant "roles differ"
    ]

let mcpMessageTests =
    testList "McpMessage" [
        testCase "creates a User message with Text content" <| fun _ ->
            let msg = { Role = User; Content = Content.text "hello" }
            Expect.equal msg.Role User "role"
            match msg.Content with
            | Text t -> Expect.equal t "hello" "text"
            | other -> failtest $"expected Text, got %A{other}"

        testCase "creates an Assistant message with Image content" <| fun _ ->
            let mime = MimeType.create "image/png" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let msg = { Role = Assistant; Content = Content.image [| 1uy |] mime }
            Expect.equal msg.Role Assistant "role"
    ]

// ───────────────────────────────────────────────────────────
//  McpError
// ───────────────────────────────────────────────────────────

let mcpErrorTests =
    testList "McpError" [
        testCase "ValidationFailed holds list of validation errors" <| fun _ ->
            match ValidationFailed [ EmptyValue "ToolName"; InvalidFormat ("ResourceUri", "bad", "valid URI") ] with
            | ValidationFailed errs -> Expect.equal (List.length errs) 2 "count"
            | _ -> failtest "expected ValidationFailed"

        testCase "ToolNotFound holds a ToolName" <| fun _ ->
            let tn = ToolName.create "missing" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            match ToolNotFound tn with
            | ToolNotFound name -> Expect.equal (ToolName.value name) "missing" "name"
            | _ -> failtest "expected ToolNotFound"

        testCase "TransportError holds a message" <| fun _ ->
            match TransportError "connection lost" with
            | TransportError msg -> Expect.equal msg "connection lost" "msg"
            | _ -> failtest "expected TransportError"

        testCase "ProtocolError holds code and message" <| fun _ ->
            match ProtocolError (404, "not found") with
            | ProtocolError (code, msg) ->
                Expect.equal code 404 "code"
                Expect.equal msg "not found" "msg"
            | _ -> failtest "expected ProtocolError"
    ]

// ───────────────────────────────────────────────────────────
//  ToolDefinition
// ───────────────────────────────────────────────────────────

let toolDefinitionTests =
    testList "ToolDefinition" [
        testCase "creates a ToolDefinition with all fields" <| fun _ ->
            let tn = ToolName.create "echo" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let td : ToolDefinition = {
                Name = tn
                Description = "echoes input"
                InputSchema = None
                Handler = fun _ -> System.Threading.Tasks.Task.FromResult(Ok [ Content.text "echoed" ])
            }
            Expect.equal (ToolName.value td.Name) "echo" "name"
            Expect.equal td.Description "echoes input" "description"
            Expect.isNone td.InputSchema "no schema"

        testCase "handler returns Result with Content list" <| fun _ ->
            let tn = ToolName.create "greet" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let td : ToolDefinition = {
                Name = tn
                Description = "greets"
                InputSchema = None
                Handler = fun _ ->
                    System.Threading.Tasks.Task.FromResult(Ok [ Content.text "Hello!"; Content.text "How are you?" ])
            }
            let result = td.Handler Map.empty |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Ok contents -> Expect.equal (List.length contents) 2 "count"
            | Error e -> failtest $"unexpected error: %A{e}"
    ]

// ───────────────────────────────────────────────────────────
//  ResourceDefinition & PromptDefinition
// ───────────────────────────────────────────────────────────

let resourceDefinitionTests =
    testList "ResourceDefinition" [
        testCase "creates with all fields" <| fun _ ->
            let uri = ResourceUri.create "file:///tmp/test.txt" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let mime = MimeType.create "text/plain" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let rd : ResourceDefinition = {
                Uri = uri; Name = "test"; Description = Some "a test"; MimeType = Some mime
                Handler = fun _ -> System.Threading.Tasks.Task.FromResult(Ok (TextResource (uri, mime, "hello")))
            }
            Expect.equal rd.Name "test" "name"
            Expect.isSome rd.Description "has description"
            Expect.isSome rd.MimeType "has mime"

        testCase "creates with optional fields as None" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/data" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let rd : ResourceDefinition = {
                Uri = uri; Name = "json"; Description = None; MimeType = None
                Handler = fun _ ->
                    let m = MimeType.create "application/json" |> Result.defaultWith (fun e -> failtest $"%A{e}")
                    System.Threading.Tasks.Task.FromResult(Ok (TextResource (uri, m, "{}")))
            }
            Expect.isNone rd.Description "no description"
            Expect.isNone rd.MimeType "no mime"
    ]

let promptTests =
    testList "Prompt types" [
        testCase "PromptArgument has name, description, and required flag" <| fun _ ->
            let arg : PromptArgument = { Name = "topic"; Description = Some "the topic"; Required = true }
            Expect.equal arg.Name "topic" "name"
            Expect.isSome arg.Description "has description"
            Expect.isTrue arg.Required "is required"

        testCase "PromptDefinition handler returns McpMessage list" <| fun _ ->
            let pn = PromptName.create "greet" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let pd : PromptDefinition = {
                Name = pn; Description = None
                Arguments = [ { Name = "name"; Description = None; Required = false } ]
                Handler = fun args ->
                    let name = args |> Map.tryFind "name" |> Option.defaultValue "World"
                    System.Threading.Tasks.Task.FromResult(Ok [
                        { Role = User; Content = Content.text $"Greet {name}" }
                        { Role = Assistant; Content = Content.text $"Hello, {name}!" }
                    ])
            }
            let result = pd.Handler (Map.ofList ["name", "Alice"]) |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Ok messages ->
                Expect.equal (List.length messages) 2 "count"
                Expect.equal messages.[0].Role User "first role"
                Expect.equal messages.[1].Role Assistant "second role"
            | Error e -> failtest $"unexpected error: %A{e}"
    ]

// ───────────────────────────────────────────────────────────
//  FsCheck property tests
// ───────────────────────────────────────────────────────────

let fsCheckConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

let domainPropertyTests =
    testList "Domain types property tests" [
        testPropertyWithConfig fsCheckConfig "Content.text always produces Text case"
            <| fun (s: string) ->
                match Content.text s with
                | Text t -> t = s
                | _ -> false
    ]

[<Tests>]
let allTypesTests =
    testList "Types" [
        contentConstructionTests
        resourceContentsTests
        mcpRoleTests
        mcpMessageTests
        mcpErrorTests
        toolDefinitionTests
        resourceDefinitionTests
        promptTests
        domainPropertyTests
    ]
