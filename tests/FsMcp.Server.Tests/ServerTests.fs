module FsMcp.Server.Tests.ServerBuilderTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

let unwrap result =
    match result with
    | Ok v -> v
    | Error e -> failtest $"unexpected error: %A{e}"

let mkTool name =
    Tool.define name $"Tool {name}" (fun _ ->
        System.Threading.Tasks.Task.FromResult(Ok [ Content.text "ok" ]))
    |> unwrap

let mkResource uri =
    Resource.define uri $"Resource {uri}" (fun _ ->
        let ru = ResourceUri.create uri |> unwrap
        let mt = MimeType.create "text/plain" |> unwrap
        System.Threading.Tasks.Task.FromResult(Ok (TextResource (ru, mt, "data"))))
    |> unwrap

let mkPrompt name =
    Prompt.define name [] (fun _ ->
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
                useStdio
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
                useStdio
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
                useStdio
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
                        useStdio
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
                        useStdio
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
                        useStdio
                    } |> ignore)
                "duplicate prompt names should fail"

        testCase "sets HTTP transport" <| fun _ ->
            let config = mcpServer {
                name "HttpServer"
                version "1.0.0"
                useHttp (Some "/mcp")
            }
            match config.Transport with
            | Http (Some ep) -> Expect.equal ep "/mcp" "endpoint"
            | other -> failtest $"expected Http, got %A{other}"

        testCase "defaults to Stdio transport" <| fun _ ->
            let config = mcpServer {
                name "Default"
                version "1.0.0"
                useStdio
            }
            match config.Transport with
            | Stdio -> ()
            | other -> failtest $"expected Stdio, got %A{other}"

        testCase "ServerConfig.validate returns Ok for valid config" <| fun _ ->
            let config : ServerConfig = {
                Name = ServerName.create "test" |> unwrap
                Version = ServerVersion.create "1.0" |> unwrap
                Tools = [ mkTool "a"; mkTool "b" ]
                Resources = []
                Prompts = []
                Middleware = []
                Transport = Stdio
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
                Transport = Stdio
            }
            match ServerConfig.validate config with
            | Error (DuplicateEntry ("Tool", "dup")) -> ()
            | other -> failtest $"expected DuplicateEntry, got %A{other}"
    ]
