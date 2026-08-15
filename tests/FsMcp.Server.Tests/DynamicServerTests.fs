#nowarn "44"

module FsMcp.Server.Tests.DynamicServerTests

open Expecto
open FsMcp.Server

let private config : ServerConfig =
    mcpServer {
        name "dynamic-migration"
        version "2.0"
    }

[<Tests>]
let dynamicServerTests =
    testList "DynamicServer" [
        testCase "legacy dynamic registration fails closed" <| fun _ ->
            Expect.throwsT<FsMcpConfigException>
                (fun () -> DynamicServer.create config |> ignore)
                "mutating a detached configuration must not appear to update the live server"
    ]
