# Feature Specification: MCP Testing Utilities

**Feature Branch**: `002-mcp-testing-utilities`
**Created**: 2026-04-02
**Status**: Draft
**Input**: User description: "MCP Testing Utilities — in-memory transport, test server/client builders, assertion helpers, and mock fixtures for testing F# MCP servers built with the FsMcp toolkit"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Test a Tool Handler in Isolation (Priority: P1)

A developer has built an MCP server with the FsMcp toolkit (feature 001)
and wants to write an Expecto test that verifies their tool handler
returns correct results for known inputs. They use an in-memory transport
to spin up the server and a test client in the same process, invoke the
tool, and assert on the response — all without opening network sockets
or spawning child processes.

**Why this priority**: This is the most common testing need. Every MCP
server developer needs to verify that tool handlers produce correct
output. Without in-memory transport, tests require process spawning and
port allocation, making them slow, flaky, and unsuitable for CI. This
single capability delivers the majority of testing value.

**Independent Test**: Can be fully tested by defining a minimal MCP
server with one tool, starting it via in-memory transport, connecting a
test client, invoking the tool with known arguments, and asserting the
response matches expectations — all within a single Expecto test.

**Acceptance Scenarios**:

1. **Given** a server definition with a tool that adds two numbers,
   **When** the developer creates an in-memory transport pair and
   connects a test client, **Then** the client can invoke the tool and
   receive the correct sum without any network I/O.
2. **Given** a server with a tool that returns an error for invalid
   input, **When** the test client invokes the tool with invalid
   arguments, **Then** the test client receives a structured error
   result that can be matched against expected error details.
3. **Given** a server definition, **When** the developer calls a helper
   function to create a connected test client, **Then** the entire
   setup (transport, server start, client connect, handshake) completes
   in a single function call returning a ready-to-use client.
4. **Given** multiple Expecto tests running in parallel, **When** each
   test creates its own in-memory transport and test client, **Then**
   all tests run in isolation without interference or shared state.

---

### User Story 2 - Assert on MCP Response Structure (Priority: P2)

A developer wants to write concise assertions that verify MCP responses
match expected shapes — checking content types, text values, error
codes, resource metadata, or prompt arguments — without manually
destructuring nested result types. They use FsMcp assertion helpers that
produce clear failure messages showing expected vs. actual values.

**Why this priority**: Without assertion helpers, every test must
manually pattern-match on `Result`, `Content` discriminated unions, and
nested option types. This creates verbose, repetitive test code and
obscure failure messages. Assertion helpers make tests readable and
failures diagnosable.

**Independent Test**: Can be tested by writing Expecto tests that
intentionally trigger assertion failures and verifying the failure
messages contain expected vs. actual values in a human-readable format.

**Acceptance Scenarios**:

1. **Given** a tool invocation result containing text content, **When**
   the developer uses `shouldHaveTextContent "expected text"` on the
   result, **Then** the assertion passes if the text matches and fails
   with a clear message showing expected vs. actual if it does not.
2. **Given** a tool invocation result that is an error, **When** the
   developer uses `shouldBeToolError` on the result, **Then** the
   assertion passes and extracts the error for further inspection.
3. **Given** a list of tool definitions from `tools/list`, **When** the
   developer uses `shouldContainTool "toolName"`, **Then** the assertion
   passes if a tool with that name exists and fails with the list of
   actual tool names if not.
4. **Given** a resource read result, **When** the developer uses
   `shouldHaveMimeType "application/json"`, **Then** the assertion
   passes if the MIME type matches.

---

### User Story 3 - Test Server with Mock Dependencies (Priority: P3)

A developer has a tool handler that depends on external services (file
system, HTTP API, database). They want to test the handler by injecting
mock dependencies through the server builder, verifying the handler
calls the dependencies correctly and handles their responses (including
failures) appropriately.

**Why this priority**: Real tool handlers have side effects. Testing
them end-to-end without mocks either requires real external services
(slow, flaky) or produces untestable code. Mock fixtures enable isolated
testing of handler logic with controlled dependency behavior.

**Independent Test**: Can be tested by defining a tool handler that
calls a dependency interface, providing a mock implementation that
records calls, running the tool through in-memory transport, and
verifying the mock was called with expected arguments.

**Acceptance Scenarios**:

1. **Given** a tool handler that reads a file via an injected
   `IFileReader` interface, **When** the developer provides a mock
   `IFileReader` that returns a known string, **Then** the tool
   handler uses the mock and the test verifies the tool output matches
   the mock data.
2. **Given** a mock dependency configured to throw an exception,
   **When** the tool is invoked, **Then** the server returns a
   structured MCP error and the test can assert on the error details.
3. **Given** a mock dependency with a call recorder, **When** the tool
   is invoked with specific arguments, **Then** the test can inspect the
   recorded calls to verify the handler passed the correct arguments to
   the dependency.

---

### User Story 4 - Property-Test MCP Server Behavior (Priority: P4)

A developer wants to use FsCheck to verify that their tool handler
behaves correctly for all valid inputs — not just hand-picked examples.
They use pre-built FsCheck generators for MCP request types (tool call
arguments, resource URIs, prompt arguments) to generate random valid
inputs and verify invariants hold across hundreds of generated cases.

**Why this priority**: Property-based testing is a constitution
requirement (Principle IV). Providing generators for MCP-specific types
saves developers from writing boilerplate generators and ensures
consistent test quality across the ecosystem.

**Independent Test**: Can be tested by writing FsCheck property tests
that use the provided generators to create random tool call arguments,
feed them to a tool handler, and verify an invariant (e.g., handler
always returns a result, never throws, result content type is always
text).

**Acceptance Scenarios**:

1. **Given** the `Arb.mcpToolCallArgs` generator, **When** it is used
   in an FsCheck property, **Then** it produces valid JSON argument
   objects with varying structures, types, and nesting depths.
2. **Given** the `Arb.mcpResourceUri` generator, **When** it is used in
   an FsCheck property, **Then** it produces valid MCP resource URIs
   matching the protocol specification.
3. **Given** a tool handler and the `Arb.mcpToolCallArgs` generator,
   **When** FsCheck runs 100+ test cases, **Then** the handler returns
   a `Result.Ok` or `Result.Error` for every input (never throws an
   unhandled exception).
4. **Given** a custom FsCheck `Arbitrary` for a user-defined input
   type, **When** the developer combines it with the provided MCP
   generators, **Then** the composed generator produces valid MCP
   requests with the custom argument type.

---

### User Story 5 - Snapshot-Test MCP Server Capabilities (Priority: P5)

A developer wants to ensure that their server's capability advertisement
(the response to `initialize` and `tools/list`) does not accidentally
change. They use a snapshot testing helper that captures the server's
capability response as a JSON file and fails if the response changes
unexpectedly — catching accidental tool renames, removed parameters, or
changed descriptions.

**Why this priority**: Capability regression is a subtle but critical
bug. If a server accidentally changes its tool list or schema, connected
clients break silently. Snapshot testing catches these regressions with
zero effort after initial setup. Lower priority because it builds on
top of the in-memory transport (US1) and is a refinement rather than a
core need.

**Independent Test**: Can be tested by creating a server, capturing its
capabilities as a snapshot, modifying the server definition, re-running
the test, and verifying it fails with a diff showing what changed.

**Acceptance Scenarios**:

1. **Given** a server and a snapshot file path, **When** the developer
   runs the snapshot test for the first time, **Then** the helper
   captures the server's `tools/list` response as a JSON file and the
   test passes.
2. **Given** an existing snapshot and an unchanged server, **When** the
   snapshot test runs, **Then** it passes without updating the file.
3. **Given** an existing snapshot and a server with a changed tool
   description, **When** the snapshot test runs, **Then** it fails with
   a human-readable diff showing the exact change.
4. **Given** an intentional change, **When** the developer runs the
   test with an "update snapshot" flag, **Then** the snapshot file is
   overwritten with the new response and the test passes.

---

### Edge Cases

- What happens when the in-memory transport is disposed before the
  client finishes a request? The transport MUST complete the pending
  request with a cancellation error, not deadlock or throw unobserved
  exceptions.
- What happens when an assertion helper is used on a `Result.Error`
  but the developer expected `Result.Ok`? The failure message MUST
  include the actual error details, not just "expected Ok, got Error."
- What happens when two tests share the same in-memory transport
  accidentally? Each test MUST create its own transport; the API MUST
  NOT expose a shared/singleton transport.
- What happens when an FsCheck generator produces an input that causes
  the tool handler to hang indefinitely? The test infrastructure MUST
  support configurable timeouts on tool invocations.
- What happens when a snapshot file contains non-deterministic data
  (timestamps, random IDs)? The snapshot helper MUST support field
  exclusion or custom comparison functions.
- What happens when the server definition changes between test runs but
  the snapshot was not updated? The test MUST fail with a clear message
  indicating the snapshot is stale, including the diff.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Library MUST provide an in-memory MCP transport that
  connects a server and client within the same process without network
  I/O.
- **FR-002**: Library MUST provide a one-call helper that creates an
  in-memory server and returns a connected, ready-to-use test client.
- **FR-003**: In-memory transports MUST be isolated per test — no
  shared state between transport instances.
- **FR-004**: Library MUST provide assertion helpers for common MCP
  response checks: text content matching, error extraction, tool list
  inspection, resource metadata validation.
- **FR-005**: Assertion helpers MUST produce clear failure messages
  showing expected vs. actual values, including context (e.g., which
  tool, which field).
- **FR-006**: Library MUST provide FsCheck `Arbitrary` generators for
  MCP request types: tool call arguments (JSON objects), resource URIs,
  and prompt arguments.
- **FR-007**: FsCheck generators MUST produce valid MCP protocol values
  that pass domain type validation (smart constructors from feature
  001).
- **FR-008**: Library MUST support dependency injection into tool
  handlers for mock-based testing, compatible with the server builder
  DSL from feature 001.
- **FR-009**: Library MUST provide a snapshot testing helper that
  captures server capabilities as JSON and detects regressions.
- **FR-010**: Snapshot helper MUST support field exclusion for
  non-deterministic values.
- **FR-011**: All test utilities MUST be asynchronous-compatible
  (`Async<'T>` or `Task<'T>`).
- **FR-012**: All test utilities MUST work with the Expecto test
  framework.
- **FR-013**: Library MUST NOT reimplement MCP protocol concerns —
  it MUST use the in-memory transport capabilities provided by the
  Microsoft `ModelContextProtocol` SDK where available.
- **FR-014**: Every public function in the testing utilities library
  MUST have corresponding Expecto tests.
- **FR-015**: Every pure function and data transformation in the
  testing utilities MUST have FsCheck property-based tests.

### Key Entities

- **InMemoryTransport**: A transport pair (server-side + client-side)
  that communicates via in-memory channels. Each instance is isolated.
  Created via a factory function, not a singleton.
- **TestClient**: A connected MCP client backed by in-memory transport,
  ready to invoke tools, read resources, and list capabilities. Created
  via a one-call helper that handles server startup and handshake.
- **AssertionHelpers**: A module of functions that extend Expecto's
  assertion vocabulary with MCP-specific checks (content type, error
  codes, tool list membership, MIME types).
- **McpArbitraries**: A module of FsCheck `Arbitrary` generators for
  MCP protocol types — tool call arguments, resource URIs, prompt
  arguments, content payloads.
- **SnapshotHelper**: A module that captures server capability responses
  as JSON files and compares against stored snapshots, supporting field
  exclusion and explicit updates.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can write a complete tool handler test (setup,
  invoke, assert) in under 10 lines of F# code using the test helpers.
- **SC-002**: In-memory transport tests execute in under 100ms each
  (no network I/O overhead).
- **SC-003**: 100% of public functions in the testing utilities library
  have Expecto tests.
- **SC-004**: 100% of pure functions and generators have FsCheck
  property tests.
- **SC-005**: Assertion failure messages always include both expected
  and actual values in human-readable format.
- **SC-006**: FsCheck generators produce valid MCP values for 100% of
  generated cases (verified by roundtrip through smart constructors).
- **SC-007**: The testing utilities library compiles and all tests pass
  via `dotnet test`.
- **SC-008**: Snapshot tests detect any change in server capabilities
  with a clear diff in the failure message.

## Assumptions

- Feature 001 (F# MCP Toolkit core) is implemented or being implemented
  concurrently. The testing utilities depend on its domain types, server
  builder, and client wrapper.
- The Microsoft `ModelContextProtocol` .NET SDK provides in-memory or
  pipe-based transport that can be used for testing. If it does not, the
  testing utilities may implement a minimal in-memory channel that
  connects two SDK stream-based transports.
- Target users are F# developers who are already using Expecto and
  FsCheck (as mandated by the constitution).
- The testing utilities are packaged as a separate F# project
  (`FsMcp.Testing`) that depends on `FsMcp` (the core library from
  feature 001).
- NuGet packaging is out of scope — consumed as a project reference.
- The testing utilities target .NET 8 or later.
- GUI-based test runners are not supported — all tests run via
  `dotnet test` or Expecto CLI.
