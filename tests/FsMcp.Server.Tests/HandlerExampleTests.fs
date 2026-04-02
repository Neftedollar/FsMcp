module FsMcp.Server.Tests.HandlerExampleTests

open Expecto
open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

[<Tests>]
let handlerExamples =
    testList "Handler examples" [
        testCase "example: echo tool that returns input as text" <| fun _ ->
            let echoTool =
                Tool.define "echo" "Echoes the input message back" (fun args ->
                    let msg =
                        args
                        |> Map.tryFind "message"
                        |> Option.map (fun j -> j.GetString())
                        |> Option.defaultValue "(no message)"
                    Task.FromResult(Ok [ Content.text $"Echo: {msg}" ]))
            match echoTool with
            | Ok td ->
                let args = Map.ofList [
                    "message", JsonDocument.Parse("\"hello world\"").RootElement
                ]
                let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.stringContains t "hello world" "echoed"
                | other -> failtest $"unexpected: %A{other}"
            | Error e -> failtest $"define failed: %A{e}"

        testCase "example: resource that returns static JSON config" <| fun _ ->
            let configResource =
                Resource.define "config://app/settings" "App Settings" (fun _ ->
                    let uri = ResourceUri.create "config://app/settings" |> Result.defaultWith (fun e -> failwith $"%A{e}")
                    let mime = MimeType.create "application/json" |> Result.defaultWith (fun e -> failwith $"%A{e}")
                    Task.FromResult(Ok (TextResource (uri, mime, """{"theme":"dark","lang":"en"}"""))))
            match configResource with
            | Ok rd ->
                let result = rd.Handler Map.empty |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok (TextResource (_, _, text)) ->
                    Expect.stringContains text "dark" "has theme"
                | other -> failtest $"unexpected: %A{other}"
            | Error e -> failtest $"define failed: %A{e}"

        testCase "example: prompt that generates a code review request" <| fun _ ->
            let reviewPrompt =
                Prompt.define "code-review"
                    [ { Name = "language"; Description = Some "programming language"; Required = true }
                      { Name = "code"; Description = Some "code to review"; Required = true } ]
                    (fun args ->
                        let lang = args |> Map.tryFind "language" |> Option.defaultValue "unknown"
                        let code = args |> Map.tryFind "code" |> Option.defaultValue ""
                        Task.FromResult(Ok [
                            { Role = User; Content = Content.text $"Please review this {lang} code:\n```\n{code}\n```" }
                            { Role = Assistant; Content = Content.text "I'll review for correctness, style, and improvements." }
                        ]))
            match reviewPrompt with
            | Ok pd ->
                Expect.equal (List.length pd.Arguments) 2 "two args"
                let result =
                    pd.Handler (Map.ofList ["language", "fsharp"; "code", "let x = 1"])
                    |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok messages ->
                    Expect.equal (List.length messages) 2 "two messages"
                    Expect.equal messages.[0].Role User "first is user"
                    Expect.equal messages.[1].Role Assistant "second is assistant"
                | Error e -> failtest $"handler error: %A{e}"
            | Error e -> failtest $"define failed: %A{e}"

        testCase "example: full server definition with mcpServer CE" <| fun _ ->
            let config = mcpServer {
                name "ExampleServer"
                version "1.0.0"

                tool (
                    Tool.define "greet" "Greets a person" (fun args ->
                        let name =
                            args |> Map.tryFind "name"
                            |> Option.map (fun j -> j.GetString())
                            |> Option.defaultValue "World"
                        Task.FromResult(Ok [ Content.text $"Hello, {name}!" ]))
                    |> Result.defaultWith (fun e -> failwith $"%A{e}"))

                tool (
                    Tool.define "add" "Adds two numbers" (fun args ->
                        let a = args |> Map.tryFind "a" |> Option.map (fun j -> j.GetDouble()) |> Option.defaultValue 0.0
                        let b = args |> Map.tryFind "b" |> Option.map (fun j -> j.GetDouble()) |> Option.defaultValue 0.0
                        Task.FromResult(Ok [ Content.text $"{a + b}" ]))
                    |> Result.defaultWith (fun e -> failwith $"%A{e}"))

                useStdio
            }
            Expect.equal (ServerName.value config.Name) "ExampleServer" "name"
            Expect.equal (List.length config.Tools) 2 "two tools"
    ]
