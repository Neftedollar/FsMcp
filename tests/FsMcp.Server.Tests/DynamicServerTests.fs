module FsMcp.Server.Tests.DynamicServerTests

open Expecto
open System.Threading.Tasks
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Server

let unwrap r = match r with Ok v -> v | Error e -> failtest $"%A{e}"

let mkTool name =
    Tool.define name $"Tool {name}" (fun _ ->
        Task.FromResult(Ok [ Content.text "ok" ]))
    |> unwrap

let mkConfig tools =
    let config : ServerConfig = {
        Name = ServerName.create "test" |> unwrap
        Version = ServerVersion.create "1.0" |> unwrap
        Tools = tools
        Resources = []
        Prompts = []
        Middleware = []
        Transport = Stdio
    }
    config

[<Tests>]
let dynamicServerTests =
    testList "DynamicServer" [
        testCase "create from config preserves tools" <| fun _ ->
            let tool = mkTool "echo"
            let config = mkConfig [ tool ]
            let dyn = DynamicServer.create config
            Expect.equal (DynamicServer.toolCount dyn) 1 "one tool"
            Expect.equal (ToolName.value dyn.Config.Tools.[0].Name) "echo" "tool name preserved"

        testCase "addTool adds a tool and fires event" <| fun _ ->
            let config = mkConfig []
            let dyn = DynamicServer.create config
            let mutable fired = false
            (DynamicServer.onToolsChanged dyn).Add(fun () -> fired <- true)
            let tool = mkTool "new-tool"
            DynamicServer.addTool tool dyn
            Expect.equal (DynamicServer.toolCount dyn) 1 "one tool after add"
            Expect.isTrue fired "event fired on add"

        testCase "removeTool removes by name and fires event" <| fun _ ->
            let tool = mkTool "removeme"
            let config = mkConfig [ tool ]
            let dyn = DynamicServer.create config
            let mutable fired = false
            (DynamicServer.onToolsChanged dyn).Add(fun () -> fired <- true)
            let tn = ToolName.create "removeme" |> unwrap
            DynamicServer.removeTool tn dyn
            Expect.equal (DynamicServer.toolCount dyn) 0 "zero tools after remove"
            Expect.isTrue fired "event fired on remove"

        testCase "addTool rejects duplicate name" <| fun _ ->
            let tool = mkTool "dup"
            let config = mkConfig [ tool ]
            let dyn = DynamicServer.create config
            Expect.throws
                (fun () -> DynamicServer.addTool (mkTool "dup") dyn)
                "duplicate tool name should fail"

        testCase "toolCount reflects changes" <| fun _ ->
            let config = mkConfig []
            let dyn = DynamicServer.create config
            Expect.equal (DynamicServer.toolCount dyn) 0 "start with zero"
            DynamicServer.addTool (mkTool "a") dyn
            Expect.equal (DynamicServer.toolCount dyn) 1 "one after add"
            DynamicServer.addTool (mkTool "b") dyn
            Expect.equal (DynamicServer.toolCount dyn) 2 "two after second add"
            let tn = ToolName.create "a" |> unwrap
            DynamicServer.removeTool tn dyn
            Expect.equal (DynamicServer.toolCount dyn) 1 "one after remove"

        testCase "onToolsChanged event fires on add and remove" <| fun _ ->
            let config = mkConfig []
            let dyn = DynamicServer.create config
            let mutable count = 0
            (DynamicServer.onToolsChanged dyn).Add(fun () -> count <- count + 1)
            DynamicServer.addTool (mkTool "x") dyn
            DynamicServer.addTool (mkTool "y") dyn
            let tn = ToolName.create "x" |> unwrap
            DynamicServer.removeTool tn dyn
            Expect.equal count 3 "event fired three times"
    ]
