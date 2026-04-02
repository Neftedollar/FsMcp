namespace FsMcp.Testing

open FsMcp.Core
open FsMcp.Core.Validation

/// Assertion helpers for testing MCP responses.
module Expect =

    /// Assert that a result is Ok, returning the inner value.
    /// Throws Expecto.AssertException if the result is Error.
    let mcpIsSuccess (message: string) (result: Result<'a, McpError>) : 'a =
        match result with
        | Ok v -> v
        | Error err ->
            Expecto.Tests.failtestf "%s: expected Ok but got Error %A" message err

    /// Assert that a result is Error, returning the McpError.
    /// Throws Expecto.AssertException if the result is Ok.
    let mcpIsError (message: string) (result: Result<'a, McpError>) : McpError =
        match result with
        | Error err -> err
        | Ok v ->
            Expecto.Tests.failtestf "%s: expected Error but got Ok %A" message v

    /// Assert that a content list has the expected count.
    let mcpHasContentCount (expected: int) (message: string) (contents: Content list) : unit =
        let actual = List.length contents
        if actual <> expected then
            Expecto.Tests.failtestf "%s: expected %d content items but got %d" message expected actual

    /// Assert that a content list contains at least one Text item matching the expected substring.
    let mcpContainsText (expected: string) (message: string) (contents: Content list) : unit =
        let texts =
            contents
            |> List.choose (fun c ->
                match c with
                | Text t -> Some t
                | _ -> None)

        let found = texts |> List.exists (fun t -> t.Contains(expected))

        if not found then
            Expecto.Tests.failtestf
                "%s: expected content list to contain text matching '%s' but found texts: %A"
                message
                expected
                texts

    /// Assert that a tool call result (Result<Content list, McpError>) contains text matching expected.
    let mcpHasTextContent (expected: string) (message: string) (result: Result<Content list, McpError>) : unit =
        let contents = mcpIsSuccess message result
        mcpContainsText expected message contents
