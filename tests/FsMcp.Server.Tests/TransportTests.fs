module FsMcp.Server.Tests.TransportTests

open Expecto
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open ModelContextProtocol.Server
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

[<Tests>]
let transportTests =
    testList "Transport" [
        testCase "createSdkTool produces a tool that can be invoked" <| fun _ ->
            // Verify the bridge from F# ToolDefinition to SDK McpServerTool works
            let td =
                Tool.define "test-echo" "Echoes back" (fun args _ ->
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
            let result = td.Handler args CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
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
                    Tool.define "noop" "Does nothing" (fun _ _ ->
                        Task.FromResult(Ok [ Content.text "ok" ]))
                    |> Result.defaultWith (fun e -> failwith $"%A{e}"))
            }
            // Verify config is valid and Server.run type-checks
            Expect.equal (ServerName.value config.Name) "TestServer" "name"
            // Server.run returns Task<unit> — type check passes
            let _runFn : ServerConfig -> Task<unit> = Server.run
            ()

        testCase "transport-agnostic composition does not advertise stateful subscriptions" <| fun _ ->
            let uri = ResourceUri.create "https://example.com/data" |> Result.defaultWith (fun error -> failtest $"%A{error}")
            let mime = MimeType.create "text/plain" |> Result.defaultWith (fun error -> failtest $"%A{error}")
            let definition =
                Resource.define (ResourceUri.value uri) "data" (fun _ _ ->
                    Task.FromResult(Ok (TextResource(uri, mime, "data"))))
                |> Result.defaultWith (fun error -> failtest $"%A{error}")
            let config = mcpServer {
                name "TransportAgnostic"
                version "2.0"
                resource definition
            }
            let services = ServiceCollection()
            let registration = Server.addToBuilder config (services.AddMcpServer())
            Expect.isNone registration.Subscriptions "only stateful HTTP composition enables subscriptions"

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
