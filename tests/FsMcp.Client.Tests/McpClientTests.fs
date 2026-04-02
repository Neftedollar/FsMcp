module FsMcp.Client.Tests.McpClientTests

open Expecto
open FsMcp.Client

[<Tests>]
let clientTransportTests =
    testList "ClientTransport" [
        testList "stdio" [
            testCase "creates StdioProcess with correct command and args" <| fun _ ->
                let transport = ClientTransport.stdio "dotnet" ["run"; "--project"; "MyServer"]
                match transport with
                | StdioProcess (cmd, args) ->
                    Expect.equal cmd "dotnet" "command"
                    Expect.equal args ["run"; "--project"; "MyServer"] "args"
                | _ -> failtest "expected StdioProcess"

            testCase "creates StdioProcess with empty args" <| fun _ ->
                let transport = ClientTransport.stdio "myserver" []
                match transport with
                | StdioProcess (cmd, args) ->
                    Expect.equal cmd "myserver" "command"
                    Expect.equal args [] "empty args"
                | _ -> failtest "expected StdioProcess"
        ]

        testList "http" [
            testCase "creates HttpEndpoint with correct URI" <| fun _ ->
                let transport = ClientTransport.http "https://localhost:8080/mcp"
                match transport with
                | HttpEndpoint (uri, headers) ->
                    Expect.equal (uri.ToString()) "https://localhost:8080/mcp" "uri"
                    Expect.equal headers Map.empty "no headers"
                | _ -> failtest "expected HttpEndpoint"

            testCase "creates HttpEndpoint with trailing slash URI" <| fun _ ->
                let transport = ClientTransport.http "https://example.com/"
                match transport with
                | HttpEndpoint (uri, _) ->
                    Expect.equal (uri.ToString()) "https://example.com/" "uri with trailing slash"
                | _ -> failtest "expected HttpEndpoint"
        ]

        testList "httpWithHeaders" [
            testCase "creates HttpEndpoint with headers" <| fun _ ->
                let headers = Map.ofList [("Authorization", "Bearer token123"); ("X-Custom", "value")]
                let transport = ClientTransport.httpWithHeaders "https://api.example.com/mcp" headers
                match transport with
                | HttpEndpoint (uri, h) ->
                    Expect.equal (uri.ToString()) "https://api.example.com/mcp" "uri"
                    Expect.equal h headers "headers"
                | _ -> failtest "expected HttpEndpoint"

            testCase "creates HttpEndpoint with empty headers" <| fun _ ->
                let transport = ClientTransport.httpWithHeaders "https://api.example.com/mcp" Map.empty
                match transport with
                | HttpEndpoint (_, h) ->
                    Expect.equal h Map.empty "empty headers"
                | _ -> failtest "expected HttpEndpoint"
        ]
    ]

[<Tests>]
let clientConfigTests =
    testList "ClientConfig" [
        testCase "can create a config with stdio transport" <| fun _ ->
            let config : ClientConfig = {
                Transport = ClientTransport.stdio "dotnet" ["run"]
                Name = "TestClient"
                ShutdownTimeout = None
            }
            Expect.equal config.Name "TestClient" "name"
            Expect.isNone config.ShutdownTimeout "no shutdown timeout"

        testCase "can create a config with http transport and timeout" <| fun _ ->
            let config : ClientConfig = {
                Transport = ClientTransport.http "https://localhost:8080"
                Name = "HttpClient"
                ShutdownTimeout = Some (System.TimeSpan.FromSeconds 10.0)
            }
            Expect.equal config.Name "HttpClient" "name"
            Expect.isSome config.ShutdownTimeout "has shutdown timeout"
            Expect.equal config.ShutdownTimeout.Value (System.TimeSpan.FromSeconds 10.0) "timeout value"
    ]

[<Tests>]
let infoTypeTests =
    testList "Info types" [
        testCase "ToolInfo can be created" <| fun _ ->
            let info : ToolInfo = { Name = "echo"; Description = "Echoes input" }
            Expect.equal info.Name "echo" "name"
            Expect.equal info.Description "Echoes input" "description"

        testCase "ResourceInfo can be created" <| fun _ ->
            let info : ResourceInfo = { Uri = "file:///tmp/data.txt"; Name = "Data File"; MimeType = Some "text/plain" }
            Expect.equal info.Uri "file:///tmp/data.txt" "uri"
            Expect.equal info.Name "Data File" "name"
            Expect.equal info.MimeType (Some "text/plain") "mimeType"

        testCase "ResourceInfo with no mime type" <| fun _ ->
            let info : ResourceInfo = { Uri = "file:///tmp/data"; Name = "Data"; MimeType = None }
            Expect.isNone info.MimeType "no mime type"

        testCase "PromptInfo can be created" <| fun _ ->
            let info : PromptInfo = { Name = "greet"; Description = Some "A greeting prompt" }
            Expect.equal info.Name "greet" "name"
            Expect.equal info.Description (Some "A greeting prompt") "description"

        testCase "PromptInfo with no description" <| fun _ ->
            let info : PromptInfo = { Name = "simple"; Description = None }
            Expect.isNone info.Description "no description"
    ]

[<Tests>]
let mcpClientIntegrationTests =
    testList "McpClient integration" [
        // Integration tests are marked as pending because they require a running MCP server.
        // These tests would verify the actual connect/listTools/callTool/etc. round-trip.
        ptestCase "connect to a stdio server" <| fun _ ->
            // Would require launching a real MCP server process
            failtest "not implemented - requires running server"

        ptestCase "list tools from a connected server" <| fun _ ->
            failtest "not implemented - requires running server"

        ptestCase "call a tool on a connected server" <| fun _ ->
            failtest "not implemented - requires running server"

        ptestCase "list resources from a connected server" <| fun _ ->
            failtest "not implemented - requires running server"

        ptestCase "read a resource from a connected server" <| fun _ ->
            failtest "not implemented - requires running server"

        ptestCase "list prompts from a connected server" <| fun _ ->
            failtest "not implemented - requires running server"

        ptestCase "get a prompt from a connected server" <| fun _ ->
            failtest "not implemented - requires running server"

        ptestCase "disconnect from a connected server" <| fun _ ->
            failtest "not implemented - requires running server"
    ]
