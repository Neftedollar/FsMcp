# Module Signatures: MCP Testing Utilities

**Feature Branch**: `002-mcp-testing-utilities`
**Created**: 2026-04-02

## Overview

This document defines the public module and function signatures for the
`FsMcp.Testing` library. These are contracts — implementation details
may vary, but the public surface must match these signatures.

## Module: FsMcp.Testing.InMemoryTransport

Creates paired in-memory streams for connecting an MCP server and client
without network I/O.

```fsharp
module FsMcp.Testing.InMemoryTransport

/// A pair of connected streams for server-client communication.
type TransportPair =
    { ServerStream: Stream
      ClientStream: Stream }
    interface IAsyncDisposable

/// Creates a new isolated transport pair.
val create : unit -> TransportPair
```

## Module: FsMcp.Testing.TestServer

High-level helpers for creating test servers with connected clients.

```fsharp
module FsMcp.Testing.TestServer

/// A running test session with server and connected client.
type TestSession =
    { Client: IMcpClient }
    interface IAsyncDisposable

/// Starts a server from the given definition and returns a session
/// with a connected client ready to invoke tools.
/// Optional configureServices allows injecting mock dependencies.
val start :
    serverDefinition: McpServerDefinition ->
    ?configureServices: (IServiceCollection -> unit) ->
    Async<TestSession>

/// Convenience: starts a server and returns just the connected client.
/// Caller is responsible for disposing the client.
val connectClient :
    serverDefinition: McpServerDefinition ->
    ?configureServices: (IServiceCollection -> unit) ->
    Async<IMcpClient>
```

## Module: FsMcp.Testing.Expect

Assertion helpers following Expecto's `Expect.___` naming convention.
All functions take a `message` parameter and throw `AssertException` on
failure with detailed expected/actual information.

```fsharp
module FsMcp.Testing.Expect

/// Asserts that the tool call result contains text content matching
/// the expected string.
val mcpHasTextContent :
    expected: string ->
    message: string ->
    result: CallToolResult ->
    unit

/// Asserts that the tool call result is an error.
/// Returns the error content for further inspection.
val mcpIsError :
    message: string ->
    result: CallToolResult ->
    string

/// Asserts that the tool call result is not an error.
val mcpIsSuccess :
    message: string ->
    result: CallToolResult ->
    unit

/// Asserts that the tool list contains a tool with the given name.
val mcpContainsTool :
    toolName: string ->
    message: string ->
    tools: McpClientTool list ->
    unit

/// Asserts that the tool list does not contain a tool with the given
/// name.
val mcpDoesNotContainTool :
    toolName: string ->
    message: string ->
    tools: McpClientTool list ->
    unit

/// Asserts that a resource has the expected MIME type.
val mcpHasMimeType :
    expected: string ->
    message: string ->
    resource: ResourceContent ->
    unit

/// Asserts that the result content contains exactly N items.
val mcpHasContentCount :
    expected: int ->
    message: string ->
    result: CallToolResult ->
    unit
```

## Module: FsMcp.Testing.McpArbitraries

FsCheck generators for MCP protocol types.

```fsharp
module FsMcp.Testing.McpArbitraries

open FsCheck

/// Generates valid JSON objects suitable for tool call arguments.
/// Depth ranges from 1-3 levels, with varying key counts and
/// value types (string, number, boolean, null, array, object).
val toolCallArgs : Arbitrary<JsonElement>

/// Generates valid MCP resource URIs.
val resourceUri : Arbitrary<string>

/// Generates valid prompt argument maps.
val promptArgs : Arbitrary<Map<string, string>>

/// Generates valid tool names that pass smart constructor validation.
val toolName : Arbitrary<string>

/// Generates valid Content discriminated union values.
val content : Arbitrary<Content>

/// Registers all MCP arbitraries with FsCheck's global registry.
/// Call this in test setup to make generators available to all
/// property tests.
val register : unit -> unit
```

## Module: FsMcp.Testing.Snapshot

Snapshot testing for MCP server capabilities.

```fsharp
module FsMcp.Testing.Snapshot

/// Result of a snapshot comparison.
type SnapshotResult =
    | Match
    | Created of path: string
    | Mismatch of expected: string * actual: string * diff: string
    | Updated of path: string

/// Verifies that the actual JSON matches the stored snapshot.
/// Creates the snapshot file on first run.
/// Updates it when FSMCP_UPDATE_SNAPSHOTS=1 is set.
val verify :
    snapshotPath: string ->
    actual: JsonElement ->
    ?exclude: string list ->
    SnapshotResult

/// Asserts that the snapshot matches (wraps verify + Expecto assert).
val shouldMatch :
    snapshotPath: string ->
    actual: JsonElement ->
    ?exclude: string list ->
    message: string ->
    unit

/// Captures the tools/list response from a test client as a snapshot.
val captureTools :
    client: IMcpClient ->
    snapshotPath: string ->
    ?exclude: string list ->
    Async<SnapshotResult>
```
