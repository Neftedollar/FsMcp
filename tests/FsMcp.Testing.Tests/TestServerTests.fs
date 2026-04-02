module FsMcp.Testing.Tests.TestServerTests

open Expecto
open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open FsMcp.Testing

// ── Helpers ──────────────────────────────────────────

let private unwrap result =
    match result with
    | Ok v -> v
    | Error e -> failtest $"unexpected error: %A{e}"

/// Build a sample ServerConfig with tools, resources, and prompts for testing.
let private sampleConfig () =
    mcpServer {
        name "TestServer"
        version "1.0.0"

        tool (
            Tool.define "echo" "Echoes the input" (fun args ->
                let msg =
                    args
                    |> Map.tryFind "message"
                    |> Option.map (fun j -> j.GetString())
                    |> Option.defaultValue "(no message)"
                Task.FromResult(Ok [ Content.text $"Echo: {msg}" ]))
            |> unwrap)

        tool (
            Tool.define "add" "Adds two numbers" (fun args ->
                let a = args |> Map.tryFind "a" |> Option.map (fun j -> j.GetDouble()) |> Option.defaultValue 0.0
                let b = args |> Map.tryFind "b" |> Option.map (fun j -> j.GetDouble()) |> Option.defaultValue 0.0
                Task.FromResult(Ok [ Content.text $"{a + b}" ]))
            |> unwrap)

        resource (
            Resource.define "file:///tmp/test.txt" "Test File" (fun _ ->
                let uri = ResourceUri.create "file:///tmp/test.txt" |> unwrap
                let mime = MimeType.create "text/plain" |> unwrap
                Task.FromResult(Ok (TextResource (uri, mime, "Hello from resource"))))
            |> unwrap)

        resource (
            Resource.define "config://app/settings" "App Settings" (fun _ ->
                let uri = ResourceUri.create "config://app/settings" |> unwrap
                let mime = MimeType.create "application/json" |> unwrap
                Task.FromResult(Ok (TextResource (uri, mime, """{"theme":"dark"}"""))))
            |> unwrap)

        prompt (
            Prompt.define "summarize"
                [ { Name = "topic"; Description = Some "The topic"; Required = true } ]
                (fun args ->
                    let topic = args |> Map.tryFind "topic" |> Option.defaultValue "unknown"
                    Task.FromResult(Ok [
                        { Role = User; Content = Content.text $"Summarize: {topic}" }
                        { Role = Assistant; Content = Content.text $"Here is a summary of {topic}." }
                    ]))
            |> unwrap)

        useStdio
    }

// ── Tests ────────────────────────────────────────────

[<Tests>]
let testServerTests =
    testList "TestServer" [

        testList "callTool" [
            testCase "finds and invokes the right tool handler" <| fun _ ->
                let config = sampleConfig ()
                let args = Map.ofList [
                    "message", JsonDocument.Parse("\"hello world\"").RootElement
                ]
                let result =
                    TestServer.callTool config "echo" args
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] ->
                    Expect.stringContains t "hello world" "should contain the echoed message"
                | other -> failtest $"unexpected result: %A{other}"

            testCase "returns ToolNotFound for unknown tool" <| fun _ ->
                let config = sampleConfig ()
                let result =
                    TestServer.callTool config "nonexistent" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (ToolNotFound tn) ->
                    Expect.equal (ToolName.value tn) "nonexistent" "tool name in error"
                | other -> failtest $"expected ToolNotFound, got: %A{other}"

            testCase "invokes the correct handler among multiple tools" <| fun _ ->
                let config = sampleConfig ()
                let args = Map.ofList [
                    "a", JsonDocument.Parse("3").RootElement
                    "b", JsonDocument.Parse("4").RootElement
                ]
                let result =
                    TestServer.callTool config "add" args
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] ->
                    Expect.equal t "7" "should be sum of 3 + 4"
                | other -> failtest $"unexpected result: %A{other}"
        ]

        testList "readResource" [
            testCase "finds and invokes the right resource handler" <| fun _ ->
                let config = sampleConfig ()
                let result =
                    TestServer.readResource config "file:///tmp/test.txt" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok (TextResource (_, _, text)) ->
                    Expect.equal text "Hello from resource" "resource text"
                | other -> failtest $"unexpected result: %A{other}"

            testCase "returns ResourceNotFound for unknown URI" <| fun _ ->
                let config = sampleConfig ()
                let result =
                    TestServer.readResource config "file:///unknown" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (ResourceNotFound ru) ->
                    Expect.equal (ResourceUri.value ru) "file:///unknown" "URI in error"
                | other -> failtest $"expected ResourceNotFound, got: %A{other}"
        ]

        testList "getPrompt" [
            testCase "finds and invokes the right prompt handler" <| fun _ ->
                let config = sampleConfig ()
                let args = Map.ofList [ "topic", "F# testing" ]
                let result =
                    TestServer.getPrompt config "summarize" args
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok messages ->
                    Expect.equal (List.length messages) 2 "two messages"
                    Expect.equal messages.[0].Role User "first is user"
                    match messages.[0].Content with
                    | Text t -> Expect.stringContains t "F# testing" "topic in prompt"
                    | _ -> failtest "expected text content"
                | Error e -> failtest $"prompt error: %A{e}"

            testCase "returns PromptNotFound for unknown prompt" <| fun _ ->
                let config = sampleConfig ()
                let result =
                    TestServer.getPrompt config "nonexistent" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (PromptNotFound pn) ->
                    Expect.equal (PromptName.value pn) "nonexistent" "prompt name in error"
                | other -> failtest $"expected PromptNotFound, got: %A{other}"
        ]

        testList "listTools" [
            testCase "returns all tool names and descriptions" <| fun _ ->
                let config = sampleConfig ()
                let tools = TestServer.listTools config
                Expect.equal (List.length tools) 2 "two tools"
                let names = tools |> List.map (fun t -> t.Name)
                Expect.contains names "echo" "has echo"
                Expect.contains names "add" "has add"
                let echo = tools |> List.find (fun t -> t.Name = "echo")
                Expect.equal echo.Description "Echoes the input" "echo description"
        ]

        testList "listResources" [
            testCase "returns all resource URIs and names" <| fun _ ->
                let config = sampleConfig ()
                let resources = TestServer.listResources config
                Expect.equal (List.length resources) 2 "two resources"
                let uris = resources |> List.map (fun r -> r.Uri)
                Expect.contains uris "file:///tmp/test.txt" "has file resource"
                Expect.contains uris "config://app/settings" "has config resource"
                let file = resources |> List.find (fun r -> r.Uri = "file:///tmp/test.txt")
                Expect.equal file.Name "Test File" "file resource name"
        ]

        testList "listPrompts" [
            testCase "returns all prompt names and descriptions" <| fun _ ->
                let config = sampleConfig ()
                let prompts = TestServer.listPrompts config
                Expect.equal (List.length prompts) 1 "one prompt"
                Expect.equal prompts.[0].Name "summarize" "prompt name"
        ]

        testList "error propagation" [
            testCase "tool handler error is propagated correctly" <| fun _ ->
                let config = mcpServer {
                    name "ErrorServer"
                    version "1.0.0"
                    tool (
                        Tool.define "fail" "Always fails" (fun _ ->
                            Task.FromResult(Error (TransportError "deliberate failure")))
                        |> unwrap)
                    useStdio
                }
                let result =
                    TestServer.callTool config "fail" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (TransportError msg) ->
                    Expect.equal msg "deliberate failure" "error message"
                | other -> failtest $"expected TransportError, got: %A{other}"

            testCase "tool handler exception is caught and wrapped" <| fun _ ->
                let config = mcpServer {
                    name "ExceptionServer"
                    version "1.0.0"
                    tool (
                        Tool.define "boom" "Throws" (fun _ ->
                            failwith "kaboom"
                            Task.FromResult(Ok []))
                        |> unwrap)
                    useStdio
                }
                let result =
                    TestServer.callTool config "boom" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (HandlerException ex) ->
                    Expect.stringContains ex.Message "kaboom" "exception message"
                | other -> failtest $"expected HandlerException, got: %A{other}"

            testCase "resource handler error is propagated correctly" <| fun _ ->
                let config = mcpServer {
                    name "ErrorServer"
                    version "1.0.0"
                    resource (
                        Resource.define "file:///err" "Error Resource" (fun _ ->
                            Task.FromResult(Error (TransportError "resource failure")))
                        |> unwrap)
                    useStdio
                }
                let result =
                    TestServer.readResource config "file:///err" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (TransportError msg) ->
                    Expect.equal msg "resource failure" "error message"
                | other -> failtest $"expected TransportError, got: %A{other}"

            testCase "prompt handler error is propagated correctly" <| fun _ ->
                let config = mcpServer {
                    name "ErrorServer"
                    version "1.0.0"
                    prompt (
                        Prompt.define "fail-prompt" [] (fun _ ->
                            Task.FromResult(Error (TransportError "prompt failure")))
                        |> unwrap)
                    useStdio
                }
                let result =
                    TestServer.getPrompt config "fail-prompt" Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (TransportError msg) ->
                    Expect.equal msg "prompt failure" "error message"
                | other -> failtest $"expected TransportError, got: %A{other}"
        ]
    ]
