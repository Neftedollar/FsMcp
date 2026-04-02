# Tasks: MCP Testing Utilities

**Input**: Design documents from `/specs/002-mcp-testing-utilities/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/module-signatures.md

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, etc.)
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the project structure and configure dependencies.

- [ ] T001 Create `src/FsMcp.Testing/FsMcp.Testing.fsproj` with
  dependencies: `FsMcp` (project ref), `ModelContextProtocol`,
  `System.IO.Pipelines`, `System.Text.Json`, `Expecto`, `FsCheck`,
  `Expecto.FsCheck`. Target `net8.0`.
- [ ] T002 Create `tests/FsMcp.Testing.Tests/FsMcp.Testing.Tests.fsproj`
  with dependencies: `FsMcp.Testing` (project ref), `FsMcp` (project
  ref), `Expecto`, `FsCheck`, `Expecto.FsCheck`. Target `net8.0`.
- [ ] T003 [P] Create `tests/FsMcp.Testing.Tests/Program.fs` with
  Expecto entry point (`[<EntryPoint>] let main argv =
  runTestsInAssemblyWithCLIArgs ...`).
- [ ] T004 [P] Add both new projects to the solution file (if one
  exists) or verify `dotnet build` and `dotnet test` work from the
  repository root.

**Checkpoint**: `dotnet build` succeeds for both projects. `dotnet test`
runs (with zero tests) for the test project.

---

## Phase 2: User Story 1 — Test a Tool Handler in Isolation (Priority: P1)

**Goal**: Provide in-memory transport and test server helpers so a
developer can spin up an MCP server and test client in-process.

**Independent Test**: Define a minimal server with one tool, start it
via in-memory transport, invoke the tool, verify the response.

### Tests for User Story 1

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T005 [P] [US1] Create
  `tests/FsMcp.Testing.Tests/InMemoryTransportTests.fs`:
  - Test: "create returns a transport pair with connected streams"
  - Test: "data written to client stream is readable from server stream"
  - Test: "data written to server stream is readable from client stream"
  - Test: "disposing one stream signals cancellation to the other"
  - Test: "each create call returns an independent isolated pair"
  - FsCheck property: "roundtrip — arbitrary bytes written to one side
    are read identically from the other"
- [ ] T006 [P] [US1] Create
  `tests/FsMcp.Testing.Tests/TestServerTests.fs`:
  - Test: "start creates a session with a connected client"
  - Test: "connectClient returns a client that can list tools"
  - Test: "connectClient returns a client that can invoke a tool and
    get the result"
  - Test: "tool invocation with invalid args returns structured error"
  - Test: "disposing TestSession cleans up server and transport"
  - Test: "parallel sessions are fully isolated"

### Implementation for User Story 1

- [ ] T007 [US1] Implement `src/FsMcp.Testing/InMemoryTransport.fs`:
  - `TransportPair` record type
  - `create` factory function using `System.IO.Pipelines.Pipe`
  - `IAsyncDisposable` implementation for cleanup
- [ ] T008 [US1] Implement `src/FsMcp.Testing/TestServer.fs`:
  - `TestSession` record type with `IAsyncDisposable`
  - `start` function: creates `InMemoryTransportPair`, wires up
    server and client transports via the SDK, starts server, connects
    client, returns `TestSession`
  - `connectClient` convenience function
  - Optional `configureServices` parameter for DI
- [ ] T009 [US1] Verify all T005 and T006 tests pass. Run
  `dotnet test` from repository root.

**Checkpoint**: A developer can create an in-memory test server, connect
a client, invoke a tool, and get results — all in-process, under 100ms.

---

## Phase 3: User Story 2 — Assert on MCP Response Structure (Priority: P2)

**Goal**: Provide Expecto-style assertion helpers for common MCP
response checks.

**Independent Test**: Write assertions that pass on correct values and
fail with clear messages on incorrect values.

### Tests for User Story 2

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T010 [P] [US2] Create
  `tests/FsMcp.Testing.Tests/ExpectTests.fs`:
  - Test: "mcpHasTextContent passes when text matches"
  - Test: "mcpHasTextContent fails with expected/actual on mismatch"
  - Test: "mcpHasTextContent fails with descriptive message when result
    is error"
  - Test: "mcpIsError passes when result is error and returns error
    text"
  - Test: "mcpIsError fails when result is success"
  - Test: "mcpIsSuccess passes when result is not error"
  - Test: "mcpIsSuccess fails with error details when result is error"
  - Test: "mcpContainsTool passes when tool exists in list"
  - Test: "mcpContainsTool fails with actual tool names when not found"
  - Test: "mcpDoesNotContainTool passes when tool is absent"
  - Test: "mcpDoesNotContainTool fails when tool is present"
  - Test: "mcpHasMimeType passes on match"
  - Test: "mcpHasMimeType fails with expected/actual on mismatch"
  - Test: "mcpHasContentCount passes on correct count"
  - Test: "mcpHasContentCount fails with expected/actual on wrong count"
  - FsCheck property: "mcpHasTextContent never throws on any
    CallToolResult (either passes or raises AssertException)"

### Implementation for User Story 2

- [ ] T011 [US2] Implement `src/FsMcp.Testing/Expect.fs`:
  - All assertion functions per contracts/module-signatures.md
  - Each function throws `Expecto.AssertException` on failure
  - Failure messages include expected value, actual value, and context
- [ ] T012 [US2] Verify all T010 tests pass. Run `dotnet test`.

**Checkpoint**: Assertion helpers produce clear pass/fail results with
actionable failure messages for all supported MCP response types.

---

## Phase 4: User Story 3 — Test Server with Mock Dependencies (Priority: P3)

**Goal**: Enable dependency injection of mock services into test servers.

**Independent Test**: Define a tool handler with an injected dependency,
provide a mock, invoke the tool, verify the mock was used correctly.

### Tests for User Story 3

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T013 [P] [US3] Add tests to
  `tests/FsMcp.Testing.Tests/TestServerTests.fs`:
  - Test: "start with configureServices injects mock dependency into
    tool handler"
  - Test: "tool handler uses injected mock and returns mock data"
  - Test: "mock dependency failure produces structured MCP error"
  - Test: "mock call recorder captures arguments passed by handler"

### Implementation for User Story 3

- [ ] T014 [US3] Extend `src/FsMcp.Testing/TestServer.fs`:
  - Ensure `configureServices` parameter is wired into the DI
    container before server startup
  - Verify DI-registered services are resolvable from tool handlers
  - No new module needed — this extends the existing `TestServer`
    module
- [ ] T015 [US3] Verify all T013 tests pass. Run `dotnet test`.

**Checkpoint**: Developers can inject mock dependencies into test
servers and verify handler-dependency interactions.

---

## Phase 5: User Story 4 — Property-Test MCP Server Behavior (Priority: P4)

**Goal**: Provide FsCheck generators for MCP protocol types so
developers can property-test their handlers.

**Independent Test**: Use generators to produce random tool call
arguments, feed them to a handler, verify invariants hold.

### Tests for User Story 4

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T016 [P] [US4] Create
  `tests/FsMcp.Testing.Tests/McpArbitrariesTests.fs`:
  - FsCheck property: "toolCallArgs generates valid JSON objects"
  - FsCheck property: "toolCallArgs shrinks to minimal counterexamples"
  - FsCheck property: "resourceUri generates valid URIs"
  - FsCheck property: "resourceUri values pass smart constructor
    validation"
  - FsCheck property: "promptArgs generates non-empty-key maps"
  - FsCheck property: "toolName generates values that pass ToolName
    smart constructor"
  - FsCheck property: "content generates all Content DU cases"
  - FsCheck property: "all generators produce values that roundtrip
    through serialization"
  - Test: "register adds all arbitraries to FsCheck global registry"

### Implementation for User Story 4

- [ ] T017 [US4] Implement `src/FsMcp.Testing/McpArbitraries.fs`:
  - `toolCallArgs` arbitrary with JSON object generation (depth 1-3,
    mixed types, 0-10 keys)
  - `resourceUri` arbitrary with valid URI generation
  - `promptArgs` arbitrary with non-empty string keys
  - `toolName` arbitrary that passes feature 001 smart constructor
  - `content` arbitrary generating all Content DU cases
  - `register` function for global FsCheck registration
  - Custom shrinkers for all generators
- [ ] T018 [US4] Verify all T016 tests pass. Run `dotnet test`.

**Checkpoint**: FsCheck generators produce valid MCP values for 100% of
generated cases, with correct shrinking.

---

## Phase 6: User Story 5 — Snapshot-Test MCP Server Capabilities (Priority: P5)

**Goal**: Provide snapshot testing that captures server capabilities as
JSON and detects regressions.

**Independent Test**: Create a server, capture snapshot, modify server,
verify test fails with diff.

### Tests for User Story 5

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T019 [P] [US5] Create
  `tests/FsMcp.Testing.Tests/SnapshotTests.fs`:
  - Test: "verify creates snapshot file on first run and returns
    Created"
  - Test: "verify returns Match when actual matches stored snapshot"
  - Test: "verify returns Mismatch with diff when actual differs"
  - Test: "verify with exclude ignores specified fields"
  - Test: "verify returns Updated when FSMCP_UPDATE_SNAPSHOTS=1 is set"
  - Test: "shouldMatch passes on matching snapshot"
  - Test: "shouldMatch fails with diff message on mismatch"
  - Test: "captureTools captures tools/list response as snapshot"
  - Test: "snapshot comparison is semantic JSON equality (ignores key
    order and whitespace)"
  - FsCheck property: "verify(create(x)) = Match for any serializable
    value x (create then verify is always Match)"

### Implementation for User Story 5

- [ ] T020 [US5] Implement `src/FsMcp.Testing/Snapshot.fs`:
  - `SnapshotResult` discriminated union
  - `verify` function with JSON semantic comparison and field exclusion
  - `shouldMatch` Expecto assertion wrapper
  - `captureTools` async helper that calls `ListToolsAsync` and
    serializes the result
  - Environment variable check for `FSMCP_UPDATE_SNAPSHOTS`
  - JSON diff generation for Mismatch messages
- [ ] T021 [US5] Verify all T019 tests pass. Run `dotnet test`.

**Checkpoint**: Snapshot testing detects capability regressions with
clear diffs and supports field exclusion.

---

## Phase 7: Polish and Cross-Cutting Concerns

**Purpose**: Integration verification and cleanup across all modules.

- [ ] T022 [P] Verify `dotnet test` passes with zero failures for all
  test projects from repository root
- [ ] T023 [P] Verify all public functions in `FsMcp.Testing` have at
  least one corresponding test (audit coverage)
- [ ] T024 [P] Verify all FsCheck generators and pure functions have
  property tests (audit coverage)
- [ ] T025 Code review: ensure no `obj` or `dynamic` in public API
  surface (Constitution Principle VI)
- [ ] T026 Code review: ensure no synchronous blocking I/O
  (Constitution Principle II)
- [ ] T027 Verify in-memory transport tests execute in <100ms each
  (SC-002)

---

## Dependencies and Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — start immediately
- **Phase 2 (US1 — In-Memory Transport + TestServer)**: Depends on
  Phase 1 completion. BLOCKS Phases 3, 4, 5, 6 because all other
  stories use in-memory transport for their tests.
- **Phase 3 (US2 — Assertion Helpers)**: Depends on Phase 2 (needs
  TestServer to create results to assert on)
- **Phase 4 (US3 — Mock Dependencies)**: Depends on Phase 2 (extends
  TestServer)
- **Phase 5 (US4 — FsCheck Generators)**: Depends on Phase 1 only
  (generators are standalone). Can run in parallel with Phase 2, but
  integration tests need Phase 2.
- **Phase 6 (US5 — Snapshot Testing)**: Depends on Phase 2 (needs
  TestServer for captureTools)
- **Phase 7 (Polish)**: Depends on all previous phases

### Within Each Phase

- Tests MUST be written and FAIL before implementation
- Implementation makes tests pass
- Phase complete when all tests pass

### Parallel Opportunities

- T001/T002 are sequential (T002 depends on T001); T003/T004 can
  parallel with each other after T002.
- T005/T006 can run in parallel (different test files)
- T010 can start in parallel with T005/T006 (different file), but
  T011 implementation needs T007/T008 to produce values to assert on
- T016 (generator tests) can start as soon as Phase 1 completes
- T019 (snapshot tests) can start as soon as Phase 1 completes
- T022/T023/T024 in Phase 7 can all run in parallel

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: In-Memory Transport + TestServer
3. **STOP and VALIDATE**: A developer can test tool handlers in-process
4. This alone delivers 80% of testing value

### Incremental Delivery

1. Phase 1 + Phase 2 = MVP (in-memory testing works)
2. + Phase 3 = tests are readable (assertion helpers)
3. + Phase 4 = tests are isolated (mock dependencies)
4. + Phase 5 = tests are thorough (property-based testing)
5. + Phase 6 = tests catch regressions (snapshot testing)
6. Each phase adds value without breaking previous phases
