module FsMcp.Server.Tests.HandlersTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

let unwrap result =
    match result with
    | Ok v -> v
    | Error e -> failtest $"unexpected error: %A{e}"

[<Tests>]
let handlersTests =
    testList "Handlers" [
        testList "Tool.define" [
            testCase "creates a valid ToolDefinition from valid name" <| fun _ ->
                let td =
                    Tool.define "echo" "Echoes input" (fun _ ->
                        System.Threading.Tasks.Task.FromResult(Ok [ Content.text "hello" ]))
                    |> unwrap
                Expect.equal (ToolName.value td.Name) "echo" "name"
                Expect.equal td.Description "Echoes input" "description"

            testCase "returns error for empty name" <| fun _ ->
                let result =
                    Tool.define "" "desc" (fun _ ->
                        System.Threading.Tasks.Task.FromResult(Ok []))
                Expect.isError result "empty name should fail"

            testCase "handler can be invoked and returns results" <| fun _ ->
                let td =
                    Tool.define "greet" "Greets" (fun _ ->
                        System.Threading.Tasks.Task.FromResult(
                            Ok [ Content.text "Hello!"; Content.text "World!" ]))
                    |> unwrap
                let output =
                    td.Handler Map.empty
                    |> Async.AwaitTask |> Async.RunSynchronously
                match output with
                | Ok contents -> Expect.equal (List.length contents) 2 "two items"
                | Error e -> failtest $"handler error: %A{e}"
        ]

        testList "Resource.define" [
            testCase "creates a valid ResourceDefinition from valid URI" <| fun _ ->
                let rd =
                    Resource.define "file:///tmp/test.txt" "Test File" (fun _ ->
                        let uri = ResourceUri.create "file:///tmp/test.txt" |> unwrap
                        let mime = MimeType.create "text/plain" |> unwrap
                        System.Threading.Tasks.Task.FromResult(
                            Ok (TextResource (uri, mime, "content"))))
                    |> unwrap
                Expect.equal (ResourceUri.value rd.Uri) "file:///tmp/test.txt" "uri"
                Expect.equal rd.Name "Test File" "name"

            testCase "returns error for invalid URI" <| fun _ ->
                let result =
                    Resource.define "not a uri" "Bad" (fun _ ->
                        System.Threading.Tasks.Task.FromResult(
                            Error (TransportError "unused")))
                Expect.isError result "invalid URI should fail"
        ]

        testList "Prompt.define" [
            testCase "creates a valid PromptDefinition from valid name" <| fun _ ->
                let pd =
                    Prompt.define "summarize"
                        [ { Name = "topic"; Description = Some "topic"; Required = true } ]
                        (fun _ ->
                            System.Threading.Tasks.Task.FromResult(
                                Ok [ { Role = Assistant; Content = Content.text "Summary" } ]))
                    |> unwrap
                Expect.equal (PromptName.value pd.Name) "summarize" "name"
                Expect.equal (List.length pd.Arguments) 1 "one arg"

            testCase "returns error for empty name" <| fun _ ->
                let result =
                    Prompt.define "" [] (fun _ ->
                        System.Threading.Tasks.Task.FromResult(Ok []))
                Expect.isError result "empty name should fail"
        ]
    ]
