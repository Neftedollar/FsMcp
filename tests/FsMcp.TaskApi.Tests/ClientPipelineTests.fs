module FsMcp.TaskApi.Tests.ClientPipelineTests

#nowarn "57"

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Client
open FsToolkit.ErrorHandling
open FsMcp.TaskApi

type private FaultingSdkClient(failure: exn) =
    inherit ModelContextProtocol.Client.McpClient()

    override _.ServerCapabilities = Unchecked.defaultof<_>
    override _.ServerInfo = Unchecked.defaultof<_>
    override _.ServerInstructions = null
    override _.Completion = Task.FromResult(Unchecked.defaultof<_>)
    override _.SessionId = null
    override _.NegotiatedProtocolVersion = null

    override _.SendRequestAsync(_request, _cancellationToken) =
        Task.FromException<ModelContextProtocol.Protocol.JsonRpcResponse>(failure)

    override _.SendMessageAsync(_message, _cancellationToken) = Task.CompletedTask
    override _.RegisterNotificationHandler(_method, _handler) = Unchecked.defaultof<IAsyncDisposable>
    override _.DisposeAsync() = ValueTask()

let private faultingEnterpriseClient failure =
    let sdkClient = new FaultingSdkClient(failure)
    {
        Client = sdkClient
        Lifetime = McpClientLifetime(sdkClient, None, None, true)
        RedactTransportFailures = true
    }

let private boxResult (pending: Task<Result<'value, McpError>>) =
    task {
        let! result = pending
        return result |> Result.map box
    }

[<Tests>]
let clientPipelineTests =
    testList "ClientPipeline" [
        testCase "callTool validates name — empty returns ValidationFailed" <| fun _ ->
            match ToolName.create "" with
            | Error (EmptyValue f) -> Expect.equal f "ToolName" "field"
            | other -> failtest $"unexpected: %A{other}"

        testCase "readResource validates URI — invalid returns InvalidFormat" <| fun _ ->
            match ResourceUri.create "not a uri" with
            | Error (InvalidFormat ("ResourceUri", _, _)) -> ()
            | other -> failtest $"unexpected: %A{other}"

        testCase "getPrompt validates name — empty returns EmptyValue" <| fun _ ->
            match PromptName.create "" with
            | Error (EmptyValue "PromptName") -> ()
            | other -> failtest $"unexpected: %A{other}"

        testCase "text extraction from Content list picks first Text" <| fun _ ->
            let contents = [ Content.text "hello"; Content.text "world" ]
            let text =
                contents
                |> List.tryPick (function Text t -> Some t | _ -> None)
                |> Option.defaultValue ""
            Expect.equal text "hello" "first text"

        testCase "text extraction returns empty when no Text content" <| fun _ ->
            let mime = MimeType.create "image/png" |> Result.defaultWith (fun e -> failtest $"%A{e}")
            let contents = [ Content.image [| 1uy |] mime ]
            let text =
                contents
                |> List.tryPick (function Text t -> Some t | _ -> None)
                |> Option.defaultValue ""
            Expect.equal text "" "empty"

        testCase "FsToolkit.ErrorHandling TaskResult.ok works" <| fun _ ->
            let result =
                FsToolkit.ErrorHandling.TaskResult.ok "hello"
                |> Async.AwaitTask |> Async.RunSynchronously
            Expect.equal result (Ok "hello") "ok"

        testCase "FsToolkit.ErrorHandling TaskResult.map works" <| fun _ ->
            let result =
                FsToolkit.ErrorHandling.TaskResult.ok 42
                |> FsToolkit.ErrorHandling.TaskResult.map (fun x -> x * 2)
                |> Async.AwaitTask |> Async.RunSynchronously
            Expect.equal result (Ok 84) "mapped"

        testCase "FsToolkit.ErrorHandling TaskResult.bind short-circuits on error" <| fun _ ->
            let result =
                FsToolkit.ErrorHandling.TaskResult.error "fail"
                |> FsToolkit.ErrorHandling.TaskResult.bind (fun (_: int) ->
                    FsToolkit.ErrorHandling.TaskResult.ok 99)
                |> Async.AwaitTask |> Async.RunSynchronously
            Expect.equal result (Error "fail") "short-circuited"

        testCase "taskResult CE compiles and chains" <| fun _ ->
            let result =
                taskResult {
                    let! x = TaskResult.ok 10
                    let! y = TaskResult.ok 20
                    return x + y
                }
                |> Async.AwaitTask |> Async.RunSynchronously
            Expect.equal result (Ok 30) "chained"

        testCase "pipeline capture preserves caller cancellation" <| fun _ ->
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            let mutable wasCanceled = false

            try
                ClientPipeline.capture cancellation.Token (fun () ->
                    Task.FromCanceled<int>(cancellation.Token))
                |> fun pending -> pending.GetAwaiter().GetResult()
                |> ignore
            with :? OperationCanceledException ->
                wasCanceled <- true

            Expect.isTrue wasCanceled "cancellation must not become HandlerException"

        testCase "pipeline capture types cancellation not requested by the caller" <| fun _ ->
            let internalCancellation = OperationCanceledException("transport stopped")

            let result =
                ClientPipeline.capture CancellationToken.None (fun () ->
                    Task.FromException<int>(internalCancellation))
                |> fun pending -> pending.GetAwaiter().GetResult()

            match result with
            | Error(HandlerException error) ->
                Expect.isTrue
                    (obj.ReferenceEquals(error, internalCancellation))
                    "the internal failure remains available inside the typed error"
            | other ->
                failtestf "Expected HandlerException for non-caller cancellation, got %A" other

        testCase "enterprise call, read, and prompt failures remain in the Result channel" <| fun _ ->
            let failure =
                EnterpriseManagedAuthorizationException
                    EnterpriseManagedAuthorizationFailure.ResourceConnectionFailed
            let client = faultingEnterpriseClient failure
            let operations = [
                fun () -> ClientPipeline.callTool "echo" Map.empty client |> boxResult
                fun () -> ClientPipeline.readResource "file:///resource" client |> boxResult
                fun () -> ClientPipeline.getPrompt "prompt" Map.empty client |> boxResult
            ]

            for operation in operations do
                match operation().GetAwaiter().GetResult() with
                | Error(HandlerException caught) ->
                    Expect.isTrue
                        (obj.ReferenceEquals(caught, failure))
                        "the typed enterprise failure is retained inside McpError"
                | other -> failtestf "Expected HandlerException, got %A" other

            match ClientPipeline.disconnect client |> fun pending -> pending.GetAwaiter().GetResult() with
            | Ok () -> ()
            | Error error -> failtestf "Disconnect failed: %A" error
    ]
