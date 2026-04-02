# Data Model: MCP Testing Utilities

**Feature Branch**: `002-mcp-testing-utilities`
**Created**: 2026-04-02

## Overview

The testing utilities library introduces a small set of types that
support test infrastructure. These types are consumed by test authors,
not by the FsMcp core library. All types defined here live in the
`FsMcp.Testing` namespace.

## Types

### TestSession

A disposable record representing a running test server with a connected
client. Created by `TestServer.start`.

**Fields**:
- `Client`: The connected MCP client (from feature 001's client wrapper)
- `ServerTransport`: The server-side transport endpoint (for lifecycle
  control)
- `ClientTransport`: The client-side transport endpoint (for lifecycle
  control)

**Behavior**:
- Implements `IAsyncDisposable` — disposing tears down client, server,
  and transport in the correct order
- Each instance is fully isolated from other instances

**Relationships**:
- Uses `InMemoryTransport` internally
- Uses feature 001's `McpClient` and `McpServerDefinition` types

---

### InMemoryTransportPair

A pair of connected transport endpoints for in-memory server-client
communication.

**Fields**:
- `ServerStream`: The stream the server reads/writes
- `ClientStream`: The stream the client reads/writes

**Behavior**:
- Created via a factory function `InMemoryTransport.create()`
- Uses `System.IO.Pipelines.Pipe` internally to connect the two
  streams
- Data written to the client stream is readable from the server stream,
  and vice versa
- Disposing either stream signals cancellation to the other

**Constraints**:
- Not thread-safe for concurrent writes on the same stream (but the
  MCP protocol is request-response, so concurrent writes don't occur)
- Each pair is isolated — no shared state with other pairs

---

### SnapshotResult

The result of a snapshot comparison.

**Cases** (discriminated union):
- `Match` — the actual response matches the stored snapshot
- `Created of path:string` — no snapshot existed; a new one was created
  at the given path
- `Mismatch of expected:string * actual:string * diff:string` — the
  response differs from the snapshot; includes the diff
- `Updated of path:string` — the snapshot was updated (when update mode
  is active)

**Behavior**:
- Used by `Snapshot.verify` to communicate results without throwing
- Assertion helpers convert `Mismatch` into Expecto assertion failures

---

### McpArbitraries (FsCheck Generators)

A static module providing FsCheck `Arbitrary` instances. Not a data type
per se, but the generators produce the following shapes:

- **ToolCallArgs**: `Arbitrary<JsonElement>` — random JSON objects with
  varying depth (1-3 levels), types (string, number, boolean, null,
  array, nested object), and key counts (0-10).
- **ResourceUri**: `Arbitrary<string>` — valid URIs matching MCP
  resource URI patterns (scheme + path, optional query/fragment).
- **PromptArgs**: `Arbitrary<Map<string, string>>` — key-value pairs
  with non-empty string keys and arbitrary string values.
- **ToolName**: `Arbitrary<string>` — non-empty strings that pass
  the `ToolName` smart constructor from feature 001.
- **Content**: `Arbitrary<Content>` — random `TextContent`,
  `ImageContent`, or `EmbeddedResourceContent` values.

**Constraints**:
- All generated values MUST pass through feature 001's smart
  constructors without error
- Shrinkers MUST produce valid values (shrinking must not produce
  values that fail validation)

## Relationships Between Types

```
TestServer.start
    │
    ├── creates InMemoryTransportPair
    │       ├── ServerStream ──► MCP Server (feature 001)
    │       └── ClientStream ──► MCP Client (feature 001)
    │
    └── returns TestSession
            ├── Client (ready to call tools)
            └── IAsyncDisposable (cleanup)

Snapshot.verify
    │
    ├── serializes server response to JSON
    ├── compares against stored .json file
    └── returns SnapshotResult

McpArbitraries
    │
    ├── ToolCallArgs  ──► used in FsCheck properties
    ├── ResourceUri   ──► used in FsCheck properties
    ├── PromptArgs    ──► used in FsCheck properties
    ├── ToolName      ──► used in FsCheck properties
    └── Content       ──► used in FsCheck properties
```

## No Persistence

The testing utilities library has no persistent storage. Snapshot files
are the only file system artifact, and they are managed by the snapshot
helper (not a database or configuration system).
