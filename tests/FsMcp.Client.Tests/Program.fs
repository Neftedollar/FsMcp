module FsMcp.Client.Tests.Program

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

let private requireValid = function
    | Ok value -> value
    | Error error -> failwith $"Invalid stdio test-server definition: %A{error}"

let private stdioTestServer () =
    let resourceUri =
        ResourceUri.create "info://fsmcp-test/status"
        |> requireValid

    let mimeType =
        MimeType.create "text/plain"
        |> requireValid

    let echoTool =
        Tool.define "echo" "Echoes one message" (fun arguments cancellationToken ->
            cancellationToken.ThrowIfCancellationRequested()

            let message =
                arguments
                |> Map.tryFind "message"
                |> Option.bind (fun value ->
                    if value.ValueKind = JsonValueKind.String then
                        value.GetString() |> Option.ofObj
                    else
                        None)
                |> Option.defaultValue ""

            Task.FromResult(Ok [ Content.text $"Echo: {message}" ]))
        |> requireValid

    let statusResource =
        Resource.define (ResourceUri.value resourceUri) "Server status" (fun _ cancellationToken ->
            cancellationToken.ThrowIfCancellationRequested()
            Task.FromResult(Ok(TextResource(resourceUri, mimeType, "running"))))
        |> requireValid

    let explainPrompt =
        Prompt.define
            "explain"
            [ {
                  Name = "topic"
                  Description = Some "Topic to explain"
                  Required = true
              } ]
            (fun arguments cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                let topic = arguments |> Map.tryFind "topic" |> Option.defaultValue "something"

                Task.FromResult(
                    Ok [
                        {
                            Role = McpRole.User
                            Content = Content.text $"Explain {topic}."
                        }
                        {
                            Role = McpRole.Assistant
                            Content = Content.text $"Explaining {topic}."
                        }
                    ]
                ))
        |> requireValid

    mcpServer {
        name "FsMcp.Client.Tests.StdioServer"
        version "2.0.0"
        tool echoTool
        resource statusResource
        prompt explainPrompt
    }

let private runStdioTestServer pidFile =
    try
        File.WriteAllText(pidFile, string Environment.ProcessId)
        Server.run (stdioTestServer ())
        |> fun pending -> pending.GetAwaiter().GetResult()
        0
    with error ->
        Console.Error.WriteLine($"FsMcp.Client.Tests stdio host failed: {error.GetType().Name}")
        1

[<EntryPoint>]
let main argv =
    match argv with
    | [| "--mcp-test-server"; pidFile |] -> runStdioTestServer pidFile
    | _ -> Tests.runTestsInAssemblyWithCLIArgs [] argv
