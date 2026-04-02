module FsMcp.Testing.Tests.ExpectTests

open Expecto
open FsMcp.Core
open FsMcp.Core.Validation
open FsMcp.Testing

// ───────────────────────────────────────────────────────
//  Helpers
// ───────────────────────────────────────────────────────

let private okTextResult text : Result<Content list, McpError> =
    Ok [ Text text ]

let private okMultiContentResult : Result<Content list, McpError> =
    let mime = MimeType.create "image/png" |> Result.defaultWith (fun _ -> failwith "bad mime")
    Ok [ Text "hello"; Image([| 1uy; 2uy |], mime); Text "world" ]

let private errorResult : Result<Content list, McpError> =
    let tn = ToolName.create "missing-tool" |> Result.defaultWith (fun _ -> failwith "bad toolname")
    Error (ToolNotFound tn)

let private validationErrorResult : Result<string, McpError> =
    Error (ValidationFailed [ EmptyValue "field" ])

let private okStringResult : Result<string, McpError> =
    Ok "success"

// ───────────────────────────────────────────────────────
//  mcpIsSuccess tests
// ───────────────────────────────────────────────────────

let mcpIsSuccessTests =
    testList "Expect.mcpIsSuccess" [
        testCase "returns inner value for Ok result" <| fun () ->
            let result = Expect.mcpIsSuccess "should succeed" okStringResult
            Expecto.Expect.equal result "success" "should extract Ok value"

        testCase "returns content list for Ok result" <| fun () ->
            let result = Expect.mcpIsSuccess "should succeed" (okTextResult "hello")
            Expecto.Expect.equal (List.length result) 1 "should have one item"

        testCase "throws for Error result" <| fun () ->
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpIsSuccess "should fail" validationErrorResult |> ignore)
                "should throw for Error"
    ]

// ───────────────────────────────────────────────────────
//  mcpIsError tests
// ───────────────────────────────────────────────────────

let mcpIsErrorTests =
    testList "Expect.mcpIsError" [
        testCase "returns McpError for Error result" <| fun () ->
            let err = Expect.mcpIsError "should be error" errorResult
            match err with
            | ToolNotFound _ -> ()
            | other -> Expecto.Tests.failtestf "expected ToolNotFound but got %A" other

        testCase "returns ValidationFailed for validation error" <| fun () ->
            let err = Expect.mcpIsError "should be error" validationErrorResult
            match err with
            | ValidationFailed errors ->
                Expecto.Expect.equal (List.length errors) 1 "should have one error"
            | other -> Expecto.Tests.failtestf "expected ValidationFailed but got %A" other

        testCase "throws for Ok result" <| fun () ->
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpIsError "should fail" okStringResult |> ignore)
                "should throw for Ok"
    ]

// ───────────────────────────────────────────────────────
//  mcpHasContentCount tests
// ───────────────────────────────────────────────────────

let mcpHasContentCountTests =
    testList "Expect.mcpHasContentCount" [
        testCase "passes when count matches" <| fun () ->
            Expect.mcpHasContentCount 1 "single item" [ Text "hello" ]

        testCase "passes for empty list with zero count" <| fun () ->
            Expect.mcpHasContentCount 0 "empty" []

        testCase "passes for multi-item list" <| fun () ->
            let mime = MimeType.create "image/png" |> Result.defaultWith (fun _ -> failwith "bad mime")
            Expect.mcpHasContentCount 3 "three items" [ Text "a"; Image([| 1uy |], mime); Text "b" ]

        testCase "throws when count does not match" <| fun () ->
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpHasContentCount 5 "wrong count" [ Text "one" ])
                "should throw for wrong count"
    ]

// ───────────────────────────────────────────────────────
//  mcpContainsText tests
// ───────────────────────────────────────────────────────

let mcpContainsTextTests =
    testList "Expect.mcpContainsText" [
        testCase "passes when exact text found" <| fun () ->
            Expect.mcpContainsText "hello" "exact match" [ Text "hello" ]

        testCase "passes when substring found" <| fun () ->
            Expect.mcpContainsText "ello" "substring match" [ Text "hello world" ]

        testCase "passes when text found among multiple contents" <| fun () ->
            let mime = MimeType.create "image/png" |> Result.defaultWith (fun _ -> failwith "bad mime")
            Expect.mcpContainsText "world" "in list" [ Text "hello"; Image([| 1uy |], mime); Text "world" ]

        testCase "throws when text not found" <| fun () ->
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpContainsText "missing" "not there" [ Text "hello" ])
                "should throw when text not found"

        testCase "throws when no Text content present" <| fun () ->
            let mime = MimeType.create "image/png" |> Result.defaultWith (fun _ -> failwith "bad mime")
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpContainsText "any" "no text" [ Image([| 1uy |], mime) ])
                "should throw when no Text content"

        testCase "throws for empty content list" <| fun () ->
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpContainsText "any" "empty" [])
                "should throw for empty list"
    ]

// ───────────────────────────────────────────────────────
//  mcpHasTextContent tests
// ───────────────────────────────────────────────────────

let mcpHasTextContentTests =
    testList "Expect.mcpHasTextContent" [
        testCase "passes for Ok result with matching text" <| fun () ->
            Expect.mcpHasTextContent "hello" "ok text" (okTextResult "hello world")

        testCase "passes for Ok multi-content with matching text" <| fun () ->
            Expect.mcpHasTextContent "world" "multi content" okMultiContentResult

        testCase "throws for Error result" <| fun () ->
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpHasTextContent "any" "error result" errorResult)
                "should throw for Error result"

        testCase "throws when text not found in Ok result" <| fun () ->
            Expecto.Expect.throwsT<Expecto.AssertException>
                (fun () -> Expect.mcpHasTextContent "missing" "no match" (okTextResult "hello"))
                "should throw when text not found"
    ]

// ───────────────────────────────────────────────────────
//  Test list
// ───────────────────────────────────────────────────────

[<Tests>]
let allExpectTests =
    testList "Expect module" [
        mcpIsSuccessTests
        mcpIsErrorTests
        mcpHasContentCountTests
        mcpContainsTextTests
        mcpHasTextContentTests
    ]
