module FsMcp.Client.Tests.ClientTests

open Expecto

[<Tests>]
let clientTests =
    testList "Client" [
        testCase "placeholder test passes" <| fun _ ->
            Expect.isTrue true "placeholder"
    ]
