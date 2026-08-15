module FsMcp.Server.Tests.StreamingTests

open Expecto
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

// ───────── Helpers ─────────

let unwrap r = match r with Ok v -> v | Error e -> failtest $"%A{e}"

/// Create an IAsyncEnumerable from a list of items.
let asyncEnumerable (items: 'T list) : IAsyncEnumerable<'T> =
    { new IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(ct) =
            let mutable index = -1
            { new IAsyncEnumerator<'T> with
                member _.Current =
                    if index >= 0 && index < items.Length then items.[index]
                    else Unchecked.defaultof<'T>
                member _.MoveNextAsync() =
                    index <- index + 1
                    ValueTask<bool>(index < items.Length)
                member _.DisposeAsync() = ValueTask.CompletedTask } }

/// Create an IAsyncEnumerable that throws after yielding some items.
let asyncEnumerableWithError (items: 'T list) (error: exn) : IAsyncEnumerable<'T> =
    { new IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(ct) =
            let mutable index = -1
            { new IAsyncEnumerator<'T> with
                member _.Current =
                    if index >= 0 && index < items.Length then items.[index]
                    else Unchecked.defaultof<'T>
                member _.MoveNextAsync() =
                    index <- index + 1
                    if index < items.Length then ValueTask<bool>(true)
                    else raise error
                member _.DisposeAsync() = ValueTask.CompletedTask } }

/// Create an IAsyncEnumerable that throws on second MoveNextAsync and tracks DisposeAsync calls.
let asyncEnumerableWithTrackedDispose
    (items: 'T list)
    (error: exn)
    (disposed: bool ref)
    : IAsyncEnumerable<'T> =
    { new IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_ct) =
            let mutable index = -1
            { new IAsyncEnumerator<'T> with
                member _.Current =
                    if index >= 0 && index < items.Length then items.[index]
                    else Unchecked.defaultof<'T>
                member _.MoveNextAsync() =
                    index <- index + 1
                    if index < items.Length then ValueTask<bool>(true)
                    else raise error
                member _.DisposeAsync() =
                    disposed.Value <- true
                    ValueTask.CompletedTask } }

// ───────── Typed args ─────────

type CountArgs = { count: int }
type StrictStreamingArgs = { count: int; enabled: bool }

// ───────── Tests ─────────

[<Tests>]
let streamingTests =
    testList "Streaming" [
        testList "StreamingTool.define" [
            testCase "collects all items from stream" <| fun _ ->
                let td =
                    StreamingTool.define "counter" "Counts" (fun _args _ ->
                        asyncEnumerable [
                            Content.text "one"
                            Content.text "two"
                            Content.text "three"
                        ])
                    |> unwrap
                Expect.equal (ToolName.value td.Name) "counter" "name"
                let result = td.Handler Map.empty CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok items ->
                    Expect.equal items.Length 3 "three items"
                    Expect.equal items.[0] (Content.text "one") "first item"
                    Expect.equal items.[1] (Content.text "two") "second item"
                    Expect.equal items.[2] (Content.text "three") "third item"
                | Error e -> failtest $"unexpected error: %A{e}"

            testCase "empty stream returns empty list" <| fun _ ->
                let td =
                    StreamingTool.define "empty" "Empty" (fun _args _ ->
                        asyncEnumerable [])
                    |> unwrap
                let result = td.Handler Map.empty CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok items -> Expect.equal items.Length 0 "empty list"
                | Error e -> failtest $"unexpected error: %A{e}"

            testCase "stream that throws returns HandlerException" <| fun _ ->
                let err = System.InvalidOperationException("boom")
                let td =
                    StreamingTool.define "failing" "Fails" (fun _args _ ->
                        asyncEnumerableWithError [ Content.text "before-error" ] err)
                    |> unwrap
                let result = td.Handler Map.empty CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Error (HandlerException ex) ->
                    Expect.equal ex.Message "boom" "exception message"
                | other -> failtest $"expected HandlerException, got: %A{other}"

            testCase "returns error for empty tool name" <| fun _ ->
                let result =
                    StreamingTool.define "" "d" (fun _ _ -> asyncEnumerable [])
                Expect.isError result "empty name"

            testCase "has no input schema" <| fun _ ->
                let td =
                    StreamingTool.define "t" "d" (fun _ _ -> asyncEnumerable [])
                    |> unwrap
                Expect.isNone td.InputSchema "no schema for untyped"
        ]

        testList "StreamingTool.defineTyped" [
            testCase "typed streaming tool collects all items" <| fun _ ->
                let td =
                    StreamingTool.defineTyped<CountArgs> "counter" "Counts" (fun args _ ->
                        let items = [ for i in 1..args.count -> Content.text $"item-{i}" ]
                        asyncEnumerable items)
                    |> unwrap
                Expect.equal (ToolName.value td.Name) "counter" "name"
                Expect.isSome td.InputSchema "has schema"
                let args = Map.ofList [
                    "count", JsonDocument.Parse("3").RootElement
                ]
                let result = td.Handler args CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok items ->
                    Expect.equal items.Length 3 "three items"
                    Expect.equal items.[0] (Content.text "item-1") "first"
                    Expect.equal items.[1] (Content.text "item-2") "second"
                    Expect.equal items.[2] (Content.text "item-3") "third"
                | Error e -> failtest $"unexpected error: %A{e}"

            testCase "typed streaming with empty result" <| fun _ ->
                let td =
                    StreamingTool.defineTyped<CountArgs> "counter" "Counts" (fun _ _ ->
                        asyncEnumerable [])
                    |> unwrap
                let args = Map.ofList [
                    "count", JsonDocument.Parse("0").RootElement
                ]
                let result = td.Handler args CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                match result with
                | Ok items -> Expect.equal items.Length 0 "empty"
                | Error e -> failtest $"unexpected error: %A{e}"

            testCase "typed streaming returns error for invalid args" <| fun _ ->
                let td =
                    StreamingTool.defineTyped<CountArgs> "counter" "Counts" (fun _ _ ->
                        asyncEnumerable [ Content.text "ok" ])
                    |> unwrap
                let args = Map.ofList [
                    "count", JsonDocument.Parse("\"not-a-number\"").RootElement
                ]
                let invalid =
                    try
                        td.Handler args CancellationToken.None
                        |> fun pending -> pending.GetAwaiter().GetResult()
                        |> ignore
                        false
                    with :? ModelContextProtocol.McpProtocolException -> true
                Expect.isTrue invalid "invalid args"

            testCase "typed streaming rejects coercible JSON strings without invocation" <| fun _ ->
                let mutable invocationCount = 0
                let td =
                    StreamingTool.defineTyped<StrictStreamingArgs> "strict" "Strict JSON" (fun _ _ ->
                        invocationCount <- invocationCount + 1
                        asyncEnumerable [])
                    |> unwrap
                let cases = [
                    "numeric string",
                    Map.ofList [
                        "count", JsonSerializer.SerializeToElement "42"
                        "enabled", JsonSerializer.SerializeToElement true
                    ]
                    "boolean string",
                    Map.ofList [
                        "count", JsonSerializer.SerializeToElement 42
                        "enabled", JsonSerializer.SerializeToElement "true"
                    ]
                ]
                for caseName, arguments in cases do
                    let error =
                        try
                            td.Handler arguments CancellationToken.None
                            |> fun pending -> pending.GetAwaiter().GetResult()
                            |> ignore
                            failtestf "Expected %s to fail" caseName
                        with :? ModelContextProtocol.McpProtocolException as caught -> caught
                    Expect.equal
                        error.ErrorCode
                        ModelContextProtocol.McpErrorCode.InvalidParams
                        $"{caseName} code"
                    Expect.equal error.Message "Invalid tool arguments." $"{caseName} message"
                Expect.equal invocationCount 0 "strict deserialization rejects before streaming starts"

            testCase "returns error for empty tool name" <| fun _ ->
                let result =
                    StreamingTool.defineTyped<CountArgs> "" "d" (fun _ _ -> asyncEnumerable [])
                Expect.isError result "empty name"
        ]

        testList "collectAsync error-path dispose" [
            testCase "collectAsync awaits enumerator dispose even when MoveNextAsync throws" <| fun _ ->
                // Track that DisposeAsync is called even after MoveNextAsync throws
                let disposed = ref false
                let err = System.InvalidOperationException("boom from MoveNext")
                // The enumerable yields one item then throws on second MoveNextAsync
                let enumerable = asyncEnumerableWithTrackedDispose [ Content.text "before" ] err disposed

                // StreamingTool.define wraps collectAsync; the exception surfaces as HandlerException
                let td =
                    StreamingTool.define "throw-on-next" "Throws" (fun _args _ -> enumerable)
                    |> unwrap
                let result = td.Handler Map.empty CancellationToken.None |> Async.AwaitTask |> Async.RunSynchronously
                // The exception must propagate
                match result with
                | Error (HandlerException ex) ->
                    Expect.equal ex.Message "boom from MoveNext" "exception propagated"
                | other -> failtest $"expected HandlerException, got: %A{other}"
                // DisposeAsync MUST have been awaited
                Expect.isTrue disposed.Value "DisposeAsync was called after exception"
        ]

        testList "mcpServer CE integration" [
            testCase "streaming tool works in mcpServer CE" <| fun _ ->
                let config = mcpServer {
                    name "StreamServer"
                    version "1.0.0"
                    tool (StreamingTool.define "stream" "Streams" (fun _ _ ->
                        asyncEnumerable [ Content.text "hello" ]) |> unwrap)
                }
                Expect.equal (List.length config.Tools) 1 "one tool"
                let result =
                    config.Tools.[0].Handler Map.empty CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                match result with
                | Ok [ Text t ] -> Expect.equal t "hello" "streamed text"
                | other -> failtest $"unexpected: %A{other}"

            testCase "typed streaming tool works in mcpServer CE" <| fun _ ->
                let config = mcpServer {
                    name "TypedStreamServer"
                    version "1.0.0"
                    tool (StreamingTool.defineTyped<CountArgs> "counter" "Counts" (fun args _ ->
                        asyncEnumerable [ for i in 1..args.count -> Content.text $"item-{i}" ]) |> unwrap)
                }
                Expect.equal (List.length config.Tools) 1 "one tool"
                Expect.isSome config.Tools.[0].InputSchema "has schema"
        ]
    ]
