#nowarn "44"

module FsMcp.Server.Tests.ServerBuilderTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server
open Microsoft.Extensions.DependencyInjection
open ModelContextProtocol.Server

let unwrap result =
    match result with
    | Ok v -> v
    | Error e -> failtest $"unexpected error: %A{e}"

let mkTool name =
    Tool.define name $"Tool {name}" (fun _ _ ->
        System.Threading.Tasks.Task.FromResult(Ok [ Content.text "ok" ]))
    |> unwrap

let mkResource uri =
    Resource.define uri $"Resource {uri}" (fun _ _ ->
        let ru = ResourceUri.create uri |> unwrap
        let mt = MimeType.create "text/plain" |> unwrap
        System.Threading.Tasks.Task.FromResult(Ok (TextResource (ru, mt, "data"))))
    |> unwrap

let mkPrompt name =
    Prompt.define name [] (fun _ _ ->
        System.Threading.Tasks.Task.FromResult(
            Ok [ { Role = Assistant; Content = Content.text "response" } ]))
    |> unwrap

[<Tests>]
let serverBuilderTests =
    testList "ServerBuilder" [
        testCase "creates a ServerConfig with tools, resources, and prompts" <| fun _ ->
            let config = mcpServer {
                name "TestServer"
                version "1.0.0"
                tool (mkTool "echo")
                resource (mkResource "file:///tmp/data.txt")
                prompt (mkPrompt "greet")
            }
            Expect.equal (ServerName.value config.Name) "TestServer" "name"
            Expect.equal (ServerVersion.value config.Version) "1.0.0" "version"
            Expect.equal (List.length config.Tools) 1 "one tool"
            Expect.equal (List.length config.Resources) 1 "one resource"
            Expect.equal (List.length config.Prompts) 1 "one prompt"

        testCase "creates a minimal ServerConfig with no handlers" <| fun _ ->
            let config = mcpServer {
                name "Minimal"
                version "0.1.0"
            }
            Expect.equal (List.length config.Tools) 0 "no tools"
            Expect.equal (List.length config.Resources) 0 "no resources"

        testCase "supports multiple tools" <| fun _ ->
            let config = mcpServer {
                name "MultiTool"
                version "1.0.0"
                tool (mkTool "echo")
                tool (mkTool "greet")
                tool (mkTool "calc")
            }
            Expect.equal (List.length config.Tools) 3 "three tools"

        testCase "rejects duplicate tool names" <| fun _ ->
            Expect.throws
                (fun () ->
                    mcpServer {
                        name "DupTool"
                        version "1.0.0"
                        tool (mkTool "echo")
                        tool (mkTool "echo")
                    } |> ignore)
                "duplicate tool names should fail"

        testCase "rejects duplicate resource URIs" <| fun _ ->
            Expect.throws
                (fun () ->
                    mcpServer {
                        name "DupResource"
                        version "1.0.0"
                        resource (mkResource "file:///tmp/a.txt")
                        resource (mkResource "file:///tmp/a.txt")
                    } |> ignore)
                "duplicate resource URIs should fail"

        testCase "rejects duplicate prompt names" <| fun _ ->
            Expect.throws
                (fun () ->
                    mcpServer {
                        name "DupPrompt"
                        version "1.0.0"
                        prompt (mkPrompt "greet")
                        prompt (mkPrompt "greet")
                    } |> ignore)
                "duplicate prompt names should fail"

        testCase "console logging is on by default" <| fun _ ->
            let config = mcpServer {
                name "Default"
                version "1.0.0"
            }
            Expect.isTrue config.ConsoleLogging "console logging should default to on"

        testCase "consoleLogging false opts out" <| fun _ ->
            let config = mcpServer {
                name "Quiet"
                version "1.0.0"
                consoleLogging false
            }
            Expect.isFalse config.ConsoleLogging "consoleLogging false should disable the console logger"

        testCase "ServerConfig.validate returns Ok for valid config" <| fun _ ->
            let config : ServerConfig = {
                Name = ServerName.create "test" |> unwrap
                Version = ServerVersion.create "1.0" |> unwrap
                Tools = [ mkTool "a"; mkTool "b" ]
                Resources = []
                Prompts = []
                Middleware = []
                ConsoleLogging = true
            }
            Expect.isOk (ServerConfig.validate config) "valid config"

        testCase "ServerConfig.validate returns Error for duplicate tools" <| fun _ ->
            let config : ServerConfig = {
                Name = ServerName.create "test" |> unwrap
                Version = ServerVersion.create "1.0" |> unwrap
                Tools = [ mkTool "dup"; mkTool "dup" ]
                Resources = []
                Prompts = []
                Middleware = []
                ConsoleLogging = true
            }
            match ServerConfig.validate config with
            | Error (DuplicateEntry ("Tool", "dup")) -> ()
            | other -> failtest $"expected DuplicateEntry, got %A{other}"

        testCase "legacy middleware declarations fail closed at registration" <| fun _ ->
            let baseConfig = mcpServer {
                name "middleware-migration"
                version "2.0"
            }
            let middleware : McpMiddleware = fun context next -> next context
            let config = { baseConfig with Middleware = [ middleware ] }
            let services = ServiceCollection()
            let builder = services.AddMcpServer()
            Expect.throwsT<FsMcpConfigException>
                (fun () -> Server.addToBuilder config builder |> ignore)
                "silently ignored middleware must not be registered"
    ]
