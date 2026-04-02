module FsMcp.Server.Tests.ServerTests

open Expecto

[<Tests>]
let serverTests =
    testList "Server" [
        testCase "placeholder test passes" <| fun _ ->
            Expect.isTrue true "placeholder"
    ]
