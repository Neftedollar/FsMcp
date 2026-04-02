# Implementation Plan: MCP Testing Utilities

**Branch**: `002-mcp-testing-utilities` | **Date**: 2026-04-02 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/002-mcp-testing-utilities/spec.md`

## Summary

Build `FsMcp.Testing`, a companion library to the FsMcp core toolkit
(feature 001) that provides in-memory transport, test server/client
helpers, Expecto assertion helpers, FsCheck generators for MCP types,
and snapshot testing — enabling F# developers to write fast, isolated,
property-based tests for their MCP servers without network I/O or
external processes.

## Technical Context

**Language/Version**: F# (.NET 8+)
**Primary Dependencies**:
- `FsMcp` (feature 001 core library — project reference)
- `ModelContextProtocol` NuGet (official C# SDK)
- `System.IO.Pipelines` (for in-memory stream pairs)
- `System.Text.Json` (for snapshot serialization)
- `Expecto` (test framework — the library extends its assertion API)
- `FsCheck` / `Expecto.FsCheck` (property-based testing)

**Storage**: File system only (snapshot `.json` files alongside tests)
**Testing**: Expecto + FsCheck (self-testing: the testing library tests
itself with the same tools it provides)
**Target Platform**: .NET 8+ (any OS)
**Project Type**: Library (consumed as project reference by test projects)
**Performance Goals**: In-memory transport tests execute in <100ms each
**Constraints**: No network I/O in test helpers; no process spawning
**Scale/Scope**: ~5 F# modules, ~200-400 LOC library + ~400-600 LOC tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Microsoft MCP Foundation | PASS | Uses SDK's stream-based transport infrastructure; does not reimplement JSON-RPC or message framing. In-memory transport creates paired streams that feed into the SDK's existing transport layer. |
| II. Idiomatic F# | PASS | Modules + let functions, pipe-friendly signatures, DU for SnapshotResult, Result/Option for expected failures. |
| III. Test-First | PASS | Every module has Expecto tests written before implementation. The library itself is tested with the same patterns it provides. |
| IV. Property-Based Testing | PASS | McpArbitraries module provides FsCheck generators. All generators and pure functions have property tests. |
| V. Extensibility | PASS | TestServer.start accepts configureServices for DI. Snapshot.verify accepts exclude list. Generators are composable with user-defined Arbitraries. |
| VI. Type Safety | PASS | SnapshotResult is a DU (not exception-based). TestSession is a typed record. Generators produce validated types. |
| VII. Simplicity | PASS | Five focused modules, each with a clear responsibility. No unnecessary abstractions. connectClient is a one-liner for simple cases. |

**Commit message rule**: No `Co-Authored-By` trailers. PASS — will enforce.

## Project Structure

### Documentation (this feature)

```text
specs/002-mcp-testing-utilities/
├── spec.md
├── plan.md              # This file
├── research.md
├── data-model.md
├── checklists/
│   └── requirements.md
├── contracts/
│   └── module-signatures.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── FsMcp/                          # Feature 001 — core library
│   └── FsMcp.fsproj
└── FsMcp.Testing/                  # Feature 002 — this feature
    ├── FsMcp.Testing.fsproj
    ├── InMemoryTransport.fs        # Paired in-memory streams
    ├── TestServer.fs               # Test session + client helpers
    ├── Expect.fs                   # MCP assertion helpers
    ├── McpArbitraries.fs           # FsCheck generators
    └── Snapshot.fs                 # Snapshot testing

tests/
├── FsMcp.Tests/                    # Feature 001 — core tests
│   └── FsMcp.Tests.fsproj
└── FsMcp.Testing.Tests/            # Feature 002 — tests for this feature
    ├── FsMcp.Testing.Tests.fsproj
    ├── InMemoryTransportTests.fs
    ├── TestServerTests.fs
    ├── ExpectTests.fs
    ├── McpArbitrariesTests.fs
    ├── SnapshotTests.fs
    └── Program.fs                  # Expecto entry point
```

**Structure Decision**: Two new projects (`FsMcp.Testing` library and
`FsMcp.Testing.Tests` test project) alongside the existing feature 001
projects. The testing library is a separate project because it has
additional dependencies (Expecto, FsCheck) that should not pollute the
core library's dependency graph. Test projects mirror source projects
1:1.

## Complexity Tracking

No constitution violations. All modules are straightforward.

| Decision | Justification |
|----------|--------------|
| Separate project (`FsMcp.Testing`) instead of adding to `FsMcp` | Avoids pulling Expecto/FsCheck into the core library. Testing utilities are optional — users who don't test (unlikely, per constitution) are not burdened. |
| `System.IO.Pipelines` for in-memory transport | The SDK does not provide an in-memory transport. Pipes are the simplest correct way to connect two stream-based transports. This is not protocol reimplementation — it's plumbing. |
