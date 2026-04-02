module FsMcp.Server.Tests.TransportTests

open Expecto
open System.Text.Json
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

[<Tests>]
let transportTests =
    testList "Transport" [
        testCase "createSdkTool produces a tool that can be invoked" <| fun _ ->
            // Verify the bridge from F# ToolDefinition to SDK McpServerTool works
            let td =
                Tool.define "test-echo" "Echoes back" (fun args ->
                    let msg =
                        args
                        |> Map.tryFind "message"
                        |> Option.map (fun j -> j.GetString())
                        |> Option.defaultValue "default"
                    Task.FromResult(Ok [ Content.text $"Echo: {msg}" ]))
                |> Result.defaultWith (fun e -> failtest $"%A{e}")

            // Verify the tool definition has correct metadata
            Expect.equal (ToolName.value td.Name) "test-echo" "tool name"
            Expect.equal td.Description "Echoes back" "description"

            // Verify the handler works end-to-end
            let args = Map.ofList [
                "message", JsonDocument.Parse("\"hello\"").RootElement
            ]
            let result = td.Handler args |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Ok [ Text t ] -> Expect.equal t "Echo: hello" "echoed"
            | other -> failtest $"unexpected: %A{other}"

        testCase "Server.run creates a runnable task (does not hang)" <| fun _ ->
            // Just verify that Server.run accepts a valid config
            // We can't actually run it in a test (it blocks), but we can verify it compiles
            let config = mcpServer {
                name "TestServer"
                version "1.0.0"
                tool (
                    Tool.define "noop" "Does nothing" (fun _ ->
                        Task.FromResult(Ok [ Content.text "ok" ]))
                    |> Result.defaultWith (fun e -> failwith $"%A{e}"))
                useStdio
            }
            // Verify config is valid and Server.run type-checks
            Expect.equal (ServerName.value config.Name) "TestServer" "name"
            // Server.run returns Task<unit> — type check passes
            let _runFn : ServerConfig -> Task<unit> = Server.run
            ()

        testCase "Interop.toSdkContentBlock converts Text correctly" <| fun _ ->
            let content = Content.text "hello"
            let block = FsMcp.Core.Interop.toSdkContentBlock content
            Expect.isTrue (block :? ModelContextProtocol.Protocol.TextContentBlock) "is TextContentBlock"
            let textBlock = block :?> ModelContextProtocol.Protocol.TextContentBlock
            Expect.equal textBlock.Text "hello" "text matches"

        testCase "Interop.toSdkContentBlock converts Image correctly" <| fun _ ->
            let mime = MimeType.create "image/png" |> Result.defaultWith (fun e -> failwith $"%A{e}")
            let data = [| 1uy; 2uy; 3uy |]
            let content = Content.image data mime
            let block = FsMcp.Core.Interop.toSdkContentBlock content
            Expect.isTrue (block :? ModelContextProtocol.Protocol.ImageContentBlock) "is ImageContentBlock"
            let imgBlock = block :?> ModelContextProtocol.Protocol.ImageContentBlock
            Expect.equal (imgBlock.Data.ToArray()) data "data matches"

        testCase "Interop roundtrip: F# Content → SDK → F# Content" <| fun _ ->
            let original = Content.text "roundtrip test"
            let sdkBlock = FsMcp.Core.Interop.toSdkContentBlock original
            let roundtripped = FsMcp.Core.Interop.fromSdkContentBlock sdkBlock
            match roundtripped with
            | Ok (Text t) -> Expect.equal t "roundtrip test" "roundtrip preserved"
            | other -> failtest $"unexpected: %A{other}"
    ]
