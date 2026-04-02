# Feature Specification: F# MCP Toolkit

**Feature Branch**: `001-fsharp-mcp-toolkit`
**Created**: 2026-04-02
**Status**: Draft
**Input**: User description: "F# MCP Toolkit — idiomatic F# wrapper library over Microsoft ModelContextProtocol .NET SDK providing server builder DSL, client wrapper, and domain types with comprehensive testing and extensibility"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Define and Run an MCP Server (Priority: P1)

A developer wants to create an MCP server that exposes custom tools,
resources, and prompts. They define the server using an F# DSL — declaring
tool names, descriptions, input schemas, and handler functions — then start
the server. An MCP client (e.g., Claude Desktop) connects and can discover
and invoke the registered tools, read resources, and use prompts.

**Why this priority**: Without a server builder, the toolkit has no core
value. This is the foundational use case that all other functionality builds
upon.

**Independent Test**: Can be fully tested by defining a minimal server with
one tool, starting it, connecting a client, invoking the tool, and verifying
the response matches expectations.

**Acceptance Scenarios**:

1. **Given** a developer writes a server definition with one tool using the
   F# DSL, **When** they start the server, **Then** the server accepts MCP
   connections and responds to `initialize` with its capabilities.
2. **Given** a running server with a registered tool, **When** a client
   calls `tools/list`, **Then** the server returns the tool with its name,
   description, and input schema.
3. **Given** a running server with a registered tool, **When** a client
   calls `tools/call` with valid arguments, **Then** the server executes the
   handler and returns the result.
4. **Given** a running server with a registered tool, **When** a client
   calls `tools/call` with invalid arguments, **Then** the server returns a
   structured error without crashing.
5. **Given** a developer defines resources and prompts alongside tools,
   **When** a client queries `resources/list` or `prompts/list`, **Then**
   the server returns the registered items with correct metadata.

---

### User Story 2 - F# Domain Types for MCP Protocol (Priority: P2)

A developer working with MCP messages wants to use idiomatic F# types
instead of raw C# classes. They import the FsMcp type definitions and work
with discriminated unions, Result types, and Option types that map to MCP
protocol concepts (tool definitions, resource descriptors, prompt messages,
content types, error codes).

**Why this priority**: Idiomatic types are the building blocks that make
both server and client ergonomic. Without them, users fall back to C#
interop patterns, defeating the purpose of the toolkit.

**Independent Test**: Can be tested by constructing domain types,
serializing them to JSON, deserializing back, and verifying roundtrip
identity. All types can be validated with FsCheck property tests.

**Acceptance Scenarios**:

1. **Given** the FsMcp types module, **When** a developer creates a tool
   definition using the F# types, **Then** it converts to/from the
   underlying C# SDK types without data loss.
2. **Given** an MCP JSON message, **When** it is deserialized into FsMcp
   types, **Then** the result is a strongly-typed discriminated union (not a
   raw object or dictionary).
3. **Given** any FsMcp domain type, **When** it is serialized and
   deserialized, **Then** the roundtrip produces an equal value (verified by
   FsCheck for all generated inputs).
4. **Given** an invalid value for a domain type (e.g., empty tool name),
   **When** a developer attempts to create it via a smart constructor,
   **Then** they receive a `Result.Error` with a descriptive validation
   message.

---

### User Story 3 - Connect to an MCP Server as a Client (Priority: P3)

A developer wants to connect to an existing MCP server (local or remote)
from F# code. They use the FsMcp client to discover available tools,
resources, and prompts, and invoke them with typed F# values. Results come
back as F# types, not raw JSON.

**Why this priority**: Client functionality completes the toolkit but
depends on types (US2) being solid. Many users will primarily build servers;
client usage is secondary but important for testing and orchestration.

**Independent Test**: Can be tested by starting a known MCP server (or a
test stub), connecting the FsMcp client, listing tools, invoking a tool, and
verifying the typed response.

**Acceptance Scenarios**:

1. **Given** a running MCP server, **When** the FsMcp client connects,
   **Then** the client completes the MCP handshake and exposes server
   capabilities as F# types.
2. **Given** a connected client, **When** the developer calls a tool by
   name with typed arguments, **Then** the client sends the request and
   returns a typed result.
3. **Given** a connected client, **When** the developer lists resources,
   **Then** the client returns a typed list of resource descriptors.
4. **Given** a connected client, **When** a tool call fails on the server,
   **Then** the client returns a `Result.Error` with the server's error
   information.

---

### User Story 4 - Extend the Toolkit with Custom Middleware (Priority: P4)

A developer wants to add cross-cutting concerns to their MCP server —
logging, metrics, authentication, or request validation — without modifying
core handler code. They compose middleware functions that intercept
requests/responses in the MCP pipeline.

**Why this priority**: Extensibility is a stated project goal, but it builds
on top of a working server (US1). Middleware is the primary extension
mechanism.

**Independent Test**: Can be tested by defining a middleware that records
all incoming tool calls, attaching it to a server, invoking a tool, and
verifying the middleware captured the call.

**Acceptance Scenarios**:

1. **Given** a middleware function that logs tool calls, **When** it is
   registered with a server and a client invokes a tool, **Then** the
   middleware executes before the handler and the log entry is recorded.
2. **Given** multiple middleware functions, **When** they are composed in
   order, **Then** they execute in the declared order (first registered =
   first to run).
3. **Given** a middleware that rejects requests based on a condition,
   **When** a rejected request arrives, **Then** the server returns an error
   without invoking the handler.

---

### User Story 5 - Comprehensive Test Suite as Documentation (Priority: P5)

A developer evaluating FsMcp wants to understand its capabilities by reading
tests. Every public function has Expecto tests, every domain type has FsCheck
generators and property tests, and every extension point has an example test
demonstrating usage.

**Why this priority**: The test suite is the living documentation of the
library. It validates correctness and teaches usage patterns simultaneously.

**Independent Test**: Can be verified by running the full test suite and
confirming all tests pass; additionally, by auditing that every public
function in the library has at least one corresponding test.

**Acceptance Scenarios**:

1. **Given** the FsMcp test project, **When** a developer runs the test
   suite, **Then** all tests pass and cover every public module.
2. **Given** any FsMcp domain type, **When** a developer looks at the test
   project, **Then** they find a FsCheck `Arbitrary` generator and at least
   one property test for that type.
3. **Given** any extensibility mechanism (middleware, custom handler),
   **When** a developer looks at the test project, **Then** they find an
   example test demonstrating how to use it.

---

### Edge Cases

- What happens when a tool handler throws an unhandled exception? The server
  MUST catch it and return a structured MCP error, not crash.
- What happens when a client connects to a server that doesn't support a
  requested capability? The client MUST return a clear error, not silently
  fail.
- What happens when a tool is registered with a duplicate name? The server
  builder MUST reject the duplicate at build time with a descriptive error.
- What happens when the upstream C# SDK changes a type? The wrapper layer
  MUST absorb the change; consumer code MUST NOT break.
- What happens when serialization encounters an unknown MCP message type?
  The system MUST preserve the raw data and return a typed "unknown message"
  variant, not discard it.

## Clarifications

### Session 2026-04-02

- Q: Primary async type for public API — `Async<'T>` or `Task<'T>`? → A: `Task<'T>` primary (matches underlying SDK); provide `Async` convenience wrappers where useful.
- Q: Project structure — single or multi-project? → A: Three projects — `FsMcp.Core` (types), `FsMcp.Server`, `FsMcp.Client`.
- Q: Built-in structured logging or logging only via middleware? → A: Built-in via `Microsoft.Extensions.Logging`; library internals emit structured logs. User middleware handles application-level logging.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Library MUST provide an F# DSL (computation expression or
  builder pattern) for defining MCP servers with tools, resources, and
  prompts.
- **FR-002**: Library MUST expose idiomatic F# domain types (discriminated
  unions, records) for all MCP protocol concepts: tool definitions, resource
  descriptors, prompt messages, content types, and error codes.
- **FR-003**: All domain types MUST have smart constructors that validate
  inputs and return `Result<'T, ValidationError>`.
- **FR-004**: Library MUST provide an F# client that connects to MCP
  servers and returns typed results.
- **FR-005**: Library MUST support a composable middleware pipeline for
  request/response interception on the server side.
- **FR-006**: All domain types MUST serialize to/from JSON with roundtrip
  fidelity (verified by FsCheck property tests).
- **FR-007**: Library MUST wrap the Microsoft `ModelContextProtocol` .NET
  SDK — protocol-level concerns (transport, JSON-RPC) MUST NOT be
  reimplemented.
- **FR-008**: Every public function MUST have corresponding Expecto tests.
- **FR-009**: Every pure function and data transformation MUST have FsCheck
  property-based tests with custom `Arbitrary` generators for domain types.
- **FR-010**: Library MUST support both stdio and SSE/HTTP transports
  (as provided by the underlying SDK).
- **FR-011**: All I/O operations MUST be asynchronous. The primary public
  API MUST use `Task<'T>` to match the underlying SDK. `Async<'T>`
  convenience wrappers SHOULD be provided where useful.
- **FR-012**: Error paths MUST use `Result<'T, 'E>` for expected failures;
  exceptions are reserved for unexpected/unrecoverable errors only.
- **FR-013**: Library MUST emit structured logs for internal operations
  (startup, transport events, errors) via `Microsoft.Extensions.Logging`.
  Users control the logging provider; application-level logging is a
  middleware concern.

### Key Entities

- **ToolDefinition**: Represents an MCP tool with a name, description,
  input schema, and handler function. Names must be non-empty and unique
  within a server.
- **ResourceDescriptor**: Represents an MCP resource with a URI, name,
  description, and MIME type. URIs must be valid and unique.
- **PromptDefinition**: Represents an MCP prompt template with a name,
  description, and argument definitions.
- **McpServer**: A configured server instance with registered tools,
  resources, prompts, and middleware. Built via the DSL.
- **McpClient**: A connection to a remote MCP server that exposes typed
  operations for tool invocation, resource reading, and prompt retrieval.
- **Middleware**: A composable function that intercepts MCP requests and/or
  responses, allowing cross-cutting concerns.
- **Content**: The payload of tool results and resource reads — text,
  images, or embedded resources, modelled as a discriminated union.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can define and start a working MCP server with
  tools, resources, and prompts in under 30 lines of F# code.
- **SC-002**: 100% of public functions have at least one Expecto test.
- **SC-003**: 100% of domain types have FsCheck `Arbitrary` generators and
  at least one property test.
- **SC-004**: All FsCheck property tests pass with 100+ generated inputs
  per property.
- **SC-005**: Roundtrip serialization (F# type → JSON → F# type) produces
  equal values for all domain types (verified by FsCheck).
- **SC-006**: The test suite runs and passes fully via `dotnet test`.
- **SC-007**: A developer can add custom middleware to a server without
  modifying any library source code.
- **SC-008**: The library compiles and all tests pass against the pinned
  version of the Microsoft `ModelContextProtocol` NuGet package.

## Assumptions

- Target users are F# developers with moderate familiarity with the MCP
  protocol concepts.
- The Microsoft `ModelContextProtocol` NuGet package is stable enough to
  build upon (breaking changes are absorbed in the wrapper layer).
- Stdio transport is the primary transport for v1; SSE/HTTP is supported
  but not the primary focus.
- The library targets .NET 8 or later.
- No GUI or visual tooling is in scope — this is a code-only library.
- NuGet packaging and publishing are out of scope for v1 (the library is
  consumed as a project reference initially).
- The library is structured as three projects: `FsMcp.Core` (domain types
  and serialization), `FsMcp.Server` (server builder DSL and middleware),
  and `FsMcp.Client` (client wrapper). Each has a corresponding test
  project.
