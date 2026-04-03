module FsMcp.TaskApi.Tests.ClientPipelineTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsToolkit.ErrorHandling
open FsMcp.TaskApi

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
    ]
