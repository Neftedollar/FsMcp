module FsMcp.Client.Tests.McpClientTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open FsMcp.Client
open FsMcp.Core
open FsMcp.Core.Validation

let private resultValue = function
    | Ok value -> value
    | Error error -> failtestf "Expected a valid value, got %A" error

let private dotnetHostPath () =
    let hostFileName =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            "dotnet.exe"
        else
            "dotnet"

    let isDotnetHost (path: string) =
        not (String.IsNullOrWhiteSpace path)
        && File.Exists path
        && String.Equals(
            Path.GetFileName path,
            hostFileName,
            StringComparison.OrdinalIgnoreCase
        )

    let configuredHost = Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
    let processHost = Environment.ProcessPath

    match [ configuredHost; processHost ] |> List.tryFind isDotnetHost with
    | Some path -> path
    | None ->
        let runtimeDirectory = DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory())

        let installationRoot =
            runtimeDirectory.Parent
            |> Option.ofObj
            |> Option.bind (fun framework -> framework.Parent |> Option.ofObj)
            |> Option.bind (fun shared -> shared.Parent |> Option.ofObj)

        match installationRoot with
        | Some root ->
            let candidate = Path.Combine(root.FullName, hostFileName)

            if isDotnetHost candidate then
                candidate
            else
                failtestf "Could not locate the current .NET host; checked %s" candidate
        | None -> failtest "Could not derive the current .NET host from the runtime directory"

let private tryReadProcessId (path: string) =
    try
        if File.Exists path then
            match Int32.TryParse(File.ReadAllText(path).Trim()) with
            | true, processId when processId > 0 && processId <> Environment.ProcessId ->
                Some processId
            | _ -> None
        else
            None
    with
    | :? IOException
    | :? UnauthorizedAccessException -> None

let private waitForChildProcess
    (pidFile: string)
    (cancellationToken: CancellationToken)
    =
    task {
        let mutable child: Process option = None

        while child.IsNone do
            cancellationToken.ThrowIfCancellationRequested()

            match tryReadProcessId pidFile with
            | Some processId ->
                try
                    child <- Some(Process.GetProcessById processId)
                with :? ArgumentException ->
                    ()
            | None -> ()

            if child.IsNone then
                do! Task.Delay(25, cancellationToken)

        return child.Value
    }

let private stopOwnedChild (child: Process option) (pidFile: string) =
    task {
        let childToStop =
            match child with
            | Some value -> Some value
            | None ->
                tryReadProcessId pidFile
                |> Option.bind (fun processId ->
                    try
                        Some(Process.GetProcessById processId)
                    with :? ArgumentException ->
                        None)

        let! processFailure =
            task {
                try
                    match childToStop with
                    | Some value when not value.HasExited ->
                        value.Kill(entireProcessTree = true)
                        do! value.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 5.0)
                    | _ -> ()

                    return None
                with
                | :? InvalidOperationException -> return None
                | error -> return Some error
            }

        let disposeFailure =
            try
                childToStop |> Option.iter (fun value -> value.Dispose())
                None
            with error ->
                Some error

        let pidFileFailure =
            try
                File.Delete pidFile
                None
            with
            | :? FileNotFoundException -> None
            | error -> Some error

        match [ processFailure; disposeFailure; pidFileFailure ] |> List.choose id with
        | [] -> ()
        | [ error ] -> ExceptionDispatchInfo.Capture(error).Throw()
        | errors -> raise (AggregateException errors)
    }

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
        testCaseAsync "real stdio round-trip covers client operations and child shutdown" <| async {
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
            let cancellationToken = timeout.Token
            let testAssembly = Reflection.Assembly.GetExecutingAssembly().Location
            let pidFile =
                Path.Combine(
                    Path.GetTempPath(),
                    $"fsmcp-client-stdio-{Guid.NewGuid():N}.pid"
                )

            let mutable connectedClient = None
            let mutable childProcess = None

            let! outcome =
                task {
                    try
                        let config = {
                            Transport =
                                ClientTransport.stdio
                                    (dotnetHostPath ())
                                    [ testAssembly; "--mcp-test-server"; pidFile ]
                            Name = "FsMcp.Client.Tests"
                            ShutdownTimeout = Some(TimeSpan.FromMilliseconds 750.0)
                        }

                        let! client =
                            McpClient.connectWithCancellation config cancellationToken

                        connectedClient <- Some client

                        let! child = waitForChildProcess pidFile cancellationToken
                        childProcess <- Some child
                        Expect.isFalse child.HasExited "stdio child is alive after initialization"

                        let! tools =
                            McpClient.listToolsWithCancellation client cancellationToken

                        let echo = tools |> List.find (fun tool -> tool.Name = "echo")
                        Expect.equal echo.Description "Echoes one message" "tool metadata round-trips"

                        let arguments =
                            Map.ofList [ "message", JsonSerializer.SerializeToElement "hello" ]

                        let! toolResult =
                            McpClient.callToolWithCancellation
                                client
                                (ToolName.create "echo" |> resultValue)
                                arguments
                                cancellationToken

                        match toolResult with
                        | Ok [ Content.Text "Echo: hello" ] -> ()
                        | other -> failtestf "Unexpected tool result: %A" other

                        let resourceUriText = "info://fsmcp-test/status"
                        let resourceUri = ResourceUri.create resourceUriText |> resultValue
                        let! resources =
                            McpClient.listResourcesWithCancellation client cancellationToken

                        let status =
                            resources
                            |> List.find (fun resource -> resource.Uri = resourceUriText)

                        Expect.equal status.Name "Server status" "resource metadata round-trips"

                        let! resourceResult =
                            McpClient.readResourceWithCancellation
                                client
                                resourceUri
                                cancellationToken

                        match resourceResult with
                        | Ok [ TextResource(actualUri, actualMimeType, "running") ] ->
                            Expect.equal
                                (ResourceUri.value actualUri)
                                resourceUriText
                                "resource URI round-trips"

                            Expect.equal
                                (MimeType.value actualMimeType)
                                "text/plain"
                                "resource MIME type round-trips"
                        | other -> failtestf "Unexpected resource result: %A" other

                        let! promptDetails =
                            McpClient.listPromptDetailsWithCancellation
                                client
                                cancellationToken

                        let explain =
                            promptDetails
                            |> List.find (fun prompt -> prompt.Name = "explain")

                        match explain.Arguments with
                        | [ argument ] ->
                            Expect.equal argument.Name "topic" "prompt argument name"
                            Expect.isTrue argument.Required "prompt required flag"
                        | other -> failtestf "Unexpected prompt arguments: %A" other

                        let! compactPrompts =
                            McpClient.listPromptsWithCancellation client cancellationToken

                        Expect.isTrue
                            (compactPrompts |> List.exists (fun prompt -> prompt.Name = "explain"))
                            "compact prompt projection includes the prompt"

                        let! promptResult =
                            McpClient.getPromptWithCancellation
                                client
                                (PromptName.create "explain" |> resultValue)
                                (Map.ofList [ "topic", "F#" ])
                                cancellationToken

                        match promptResult with
                        | Ok [
                            {
                                Role = McpRole.User
                                Content = Content.Text "Explain F#."
                            }
                            {
                                Role = McpRole.Assistant
                                Content = Content.Text "Explaining F#."
                            }
                          ] -> ()
                        | other -> failtestf "Unexpected prompt result: %A" other

                        connectedClient <- None

                        do!
                            McpClient.disconnect client
                            |> fun pending -> pending.WaitAsync(cancellationToken)

                        do! child.WaitForExitAsync(cancellationToken)
                        Expect.isTrue child.HasExited "disconnect settles the owned stdio child"
                        return Ok()
                    with error ->
                        return Error error
                }
                |> Async.AwaitTask

            let! cleanupFailure =
                task {
                    let! disconnectFailure =
                        task {
                            try
                                match connectedClient with
                                | Some client ->
                                    do!
                                        McpClient.disconnect client
                                        |> fun pending -> pending.WaitAsync(TimeSpan.FromSeconds 7.0)
                                | None -> ()

                                return None
                            with error ->
                                return Some error
                        }

                    let! childFailure =
                        task {
                            try
                                do! stopOwnedChild childProcess pidFile
                                return None
                            with error ->
                                return Some error
                        }

                    match [ disconnectFailure; childFailure ] |> List.choose id with
                    | [] -> return None
                    | [ error ] -> return Some error
                    | errors -> return Some(AggregateException errors)
                }
                |> Async.AwaitTask

            match outcome, cleanupFailure with
            | Ok (), None -> ()
            | Ok (), Some cleanupError ->
                ExceptionDispatchInfo.Capture(cleanupError).Throw()
            | Error primaryError, None ->
                ExceptionDispatchInfo.Capture(primaryError).Throw()
            | Error primaryError, Some cleanupError ->
                raise (AggregateException(primaryError, cleanupError))
        }
    ]
