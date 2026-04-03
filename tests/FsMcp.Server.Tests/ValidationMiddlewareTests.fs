module FsMcp.Server.Tests.ValidationMiddlewareTests

open Expecto
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

type GreetArgs = { name: string; greeting: string option }

let okHandler (_ctx: McpContext) : Task<McpResponse> =
    Task.FromResult(Success (JsonDocument.Parse("{}").RootElement))

let mkConfig () =
    mcpServer {
        name "TestServer"
        version "1.0.0"
        tool (TypedTool.define<GreetArgs> "greet" "Greets" (fun args -> task {
            return Ok [ Content.text $"Hello, {args.name}!" ]
        }) |> Result.defaultWith (fun e -> failwith $"%A{e}"))
        useStdio
    }

let mkCtx method' paramsJson =
    { Method = method'
      Params = paramsJson |> Option.map (fun j -> JsonDocument.Parse(j : string).RootElement)
      CancellationToken = CancellationToken.None }

[<Tests>]
let validationMiddlewareTests =
    testList "ValidationMiddleware" [
        testCase "passes when all required fields present" <| fun _ ->
            let config = mkConfig ()
            let mw = ValidationMiddleware.create config
            let ctx = mkCtx "tools/call" (Some """{"name":"greet","arguments":{"name":"World"}}""")
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Success _ -> ()
            | McpResponseError e -> failtest $"expected success, got %A{e}"

        testCase "rejects when required field missing" <| fun _ ->
            let config = mkConfig ()
            let mw = ValidationMiddleware.create config
            let ctx = mkCtx "tools/call" (Some """{"name":"greet","arguments":{}}""")
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | McpResponseError (TransportError msg) ->
                Expect.stringContains msg "name" "mentions missing field"
            | other -> failtest $"expected error, got %A{other}"

        testCase "passes when optional field missing" <| fun _ ->
            let config = mkConfig ()
            let mw = ValidationMiddleware.create config
            // "greeting" is option, so not required
            let ctx = mkCtx "tools/call" (Some """{"name":"greet","arguments":{"name":"Alice"}}""")
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Success _ -> ()
            | McpResponseError e -> failtest $"expected success, got %A{e}"

        testCase "passes through non-tool-call methods" <| fun _ ->
            let config = mkConfig ()
            let mw = ValidationMiddleware.create config
            let ctx = mkCtx "tools/list" None
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Success _ -> ()
            | other -> failtest $"expected pass-through, got %A{other}"

        testCase "passes through unknown tool names" <| fun _ ->
            let config = mkConfig ()
            let mw = ValidationMiddleware.create config
            let ctx = mkCtx "tools/call" (Some """{"name":"unknown","arguments":{}}""")
            let result = mw ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously
            match result with
            | Success _ -> ()
            | other -> failtest $"expected pass-through for unknown tool, got %A{other}"

        testCase "composes with other middleware in pipeline" <| fun _ ->
            let config = mkConfig ()
            let log = ResizeArray<string>()
            let logMw : McpMiddleware = fun ctx next -> task {
                log.Add "before"
                let! r = next ctx
                log.Add "after"
                return r
            }
            let pipeline = Middleware.pipeline [ logMw; ValidationMiddleware.create config ]
            let ctx = mkCtx "tools/call" (Some """{"name":"greet","arguments":{"name":"Bob"}}""")
            pipeline ctx okHandler |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            Expect.equal (Seq.toList log) ["before"; "after"] "middleware ran"
    ]
