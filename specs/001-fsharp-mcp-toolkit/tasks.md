# Tasks: F# MCP Toolkit

**Input**: Design documents from `/specs/001-fsharp-mcp-toolkit/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/

**Tests**: Required — Constitution Principles III and IV mandate Expecto tests for every public function and FsCheck property tests for every domain type.

**Organization**: Tasks are grouped by user story. Core types are in the Foundational phase since they block all stories.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Multi-project library**: `src/FsMcp.Core/`, `src/FsMcp.Server/`, `src/FsMcp.Client/`
- **Tests**: `tests/FsMcp.Core.Tests/`, `tests/FsMcp.Server.Tests/`, `tests/FsMcp.Client.Tests/`

---

## Phase 1: Setup

**Purpose**: Solution and project initialization

- [ ] T001 Create solution file at `FsMcp.sln` and directory structure (`src/`, `tests/`)
- [ ] T002 Create `src/FsMcp.Core/FsMcp.Core.fsproj` with dependencies: `ModelContextProtocol`, `Microsoft.Extensions.Logging.Abstractions`, `System.Text.Json`
- [ ] T003 [P] Create `src/FsMcp.Server/FsMcp.Server.fsproj` with dependencies: `FsMcp.Core`, `Microsoft.Extensions.Hosting`
- [ ] T004 [P] Create `src/FsMcp.Client/FsMcp.Client.fsproj` with dependency: `FsMcp.Core`
- [ ] T005 [P] Create `tests/FsMcp.Core.Tests/FsMcp.Core.Tests.fsproj` with dependencies: `FsMcp.Core`, `Expecto`, `Expecto.FsCheck`, `FsCheck`
- [ ] T006 [P] Create `tests/FsMcp.Server.Tests/FsMcp.Server.Tests.fsproj` with dependencies: `FsMcp.Server`, `FsMcp.Core`, `Expecto`, `Expecto.FsCheck`, `FsCheck`
- [ ] T007 [P] Create `tests/FsMcp.Client.Tests/FsMcp.Client.Tests.fsproj` with dependencies: `FsMcp.Client`, `FsMcp.Core`, `Expecto`, `Expecto.FsCheck`, `FsCheck`
- [ ] T008 Verify `dotnet build` compiles the solution and `dotnet test` runs (empty test projects)

**Checkpoint**: Solution builds, empty test runner executes

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core domain types, validation, serialization, and interop that ALL user stories depend on

**CRITICAL**: No user story work can begin until this phase is complete

### Tests for Foundational

- [ ] T009 Write Expecto tests for identifier smart constructors (ToolName, ResourceUri, PromptName, MimeType, ServerName, ServerVersion — valid/invalid/edge cases) in `tests/FsMcp.Core.Tests/ValidationTests.fs`
- [ ] T010 [P] Write Expecto tests for Content type construction helpers (text, image, resource) in `tests/FsMcp.Core.Tests/TypesTests.fs`

### Implementation for Foundational

- [ ] T011 Implement ValidationError DU and identifier single-case DUs (ToolName, ResourceUri, PromptName, MimeType, ServerName, ServerVersion) with smart constructors in `src/FsMcp.Core/Validation.fs`
- [ ] T012 Implement domain types: Content, ResourceContents, McpRole, McpMessage, McpError, ToolDefinition, ResourceDefinition, PromptArgument, PromptDefinition in `src/FsMcp.Core/Types.fs`
- [ ] T013 Implement Content convenience constructors (Content.text, Content.image, Content.resource) in `src/FsMcp.Core/Types.fs`
- [ ] T014 Verify T009 and T010 tests pass — red-green cycle complete

**Checkpoint**: Foundation ready — all domain types exist, smart constructors validated, tests green

---

## Phase 3: User Story 1 — Define and Run an MCP Server (Priority: P1) MVP

**Goal**: Developer can define an MCP server with tools/resources/prompts via F# DSL and run it

**Independent Test**: Start a server with one tool via stdio, connect a client, invoke the tool, verify response

### Tests for User Story 1

- [ ] T015 [P] [US1] Write Expecto tests for ServerConfig construction and duplicate-name rejection in `tests/FsMcp.Server.Tests/ServerBuilderTests.fs`
- [ ] T016 [P] [US1] Write Expecto tests for tool/resource/prompt handler registration and invocation in `tests/FsMcp.Server.Tests/HandlersTests.fs`
- [ ] T017 [P] [US1] Write Expecto tests for Interop module (F# types ↔ C# SDK types roundtrip) in `tests/FsMcp.Core.Tests/InteropTests.fs`

### Implementation for User Story 1

- [ ] T018 [US1] Implement Interop module: toSdkContent, fromSdkContent, toSdkResourceContents, fromSdkResourceContents in `src/FsMcp.Core/Interop.fs`
- [ ] T019 [US1] Implement ServerConfig record and Transport DU (Stdio, Http) in `src/FsMcp.Server/ServerBuilder.fs`
- [ ] T020 [US1] Implement Tool.define, Resource.define, Prompt.define convenience functions in `src/FsMcp.Server/Handlers.fs`
- [ ] T021 [US1] Implement McpServerBuilder computation expression (name, version, tool, resource, prompt, useStdio, useHttp keywords) in `src/FsMcp.Server/ServerBuilder.fs`
- [ ] T022 [US1] Implement Server.run — bridge ServerConfig to C# SDK's AddMcpServer/WithStdioServerTransport/WithHttpTransport in `src/FsMcp.Server/Transport.fs`
- [ ] T022b [US1] Wire ILogger through ServerConfig and emit structured logs for server startup, transport binding, and handler errors in `src/FsMcp.Server/Transport.fs`
- [ ] T023 [US1] Implement Server.runAsync (Async wrapper for run) in `src/FsMcp.Server/Transport.fs`
- [ ] T024 [US1] Verify T015, T016, T017 tests pass — server builds, tools register, handlers invoke correctly

**Checkpoint**: US1 complete — MCP server definable and runnable via F# DSL. Independently testable.

---

## Phase 4: User Story 2 — F# Domain Types Deep Coverage (Priority: P2)

**Goal**: Exhaustive FsCheck property tests and roundtrip serialization for all domain types

**Independent Test**: Run FsCheck property tests — all pass with 100+ generated inputs per property

### Tests for User Story 2

- [ ] T025 [US2] Write FsCheck Arbitrary generators for all identifier types (ToolName, ResourceUri, PromptName, MimeType, ServerName, ServerVersion) in `tests/FsMcp.Core.Tests/Generators.fs`
- [ ] T026 [US2] Write FsCheck Arbitrary generators for Content, ResourceContents, McpRole, McpMessage, McpError, ToolDefinition, ResourceDefinition, PromptDefinition in `tests/FsMcp.Core.Tests/Generators.fs`
- [ ] T027 [US2] Write FsCheck property tests: roundtrip (serialize → deserialize = identity) for all domain types in `tests/FsMcp.Core.Tests/PropertyTests.fs`
- [ ] T028 [P] [US2] Write FsCheck property tests: invariants (smart constructor rules hold for all generated inputs) in `tests/FsMcp.Core.Tests/PropertyTests.fs`
- [ ] T029 [P] [US2] Write FsCheck property tests: interop roundtrip (F# type → C# SDK type → F# type = identity) in `tests/FsMcp.Core.Tests/PropertyTests.fs`

### Implementation for User Story 2

- [ ] T030 [US2] Implement JSON serialization: custom JsonConverters for Content, ResourceContents, McpMessage DUs matching MCP wire format in `src/FsMcp.Core/Serialization.fs`
- [ ] T031 [US2] Implement jsonOptions (pre-configured JsonSerializerOptions) and serialize/deserialize helper functions in `src/FsMcp.Core/Serialization.fs`
- [ ] T032 [P] [US2] Write Expecto example-based serialization tests (known JSON → F# type, F# type → expected JSON) in `tests/FsMcp.Core.Tests/SerializationTests.fs`
- [ ] T033 [US2] Verify all T025–T029, T032 tests pass — FsCheck generators produce valid types, all properties hold, serialization roundtrips

**Checkpoint**: US2 complete — every domain type has generators, property tests, and serialization. Independently verifiable via `dotnet test`.

---

## Phase 5: User Story 3 — Connect to MCP Server as Client (Priority: P3)

**Goal**: Developer can connect to an MCP server from F# and get typed results

**Independent Test**: Start a test server, connect FsMcp client, list tools, call a tool, verify typed response

### Tests for User Story 3

- [ ] T034 [P] [US3] Write Expecto tests for ClientTransport construction helpers (stdio, http, httpWithHeaders) in `tests/FsMcp.Client.Tests/McpClientTests.fs`
- [ ] T035 [P] [US3] Write Expecto integration tests: create a minimal test server (reusing US1 server builder), connect FsMcp client, exercise listTools, callTool, listResources, readResource, listPrompts, getPrompt in `tests/FsMcp.Client.Tests/McpClientTests.fs`

### Implementation for User Story 3

- [ ] T036 [US3] Implement ClientTransport DU (StdioProcess, HttpEndpoint) and Transport creation helpers (stdio, http, httpWithHeaders) in `src/FsMcp.Client/ClientTransport.fs`
- [ ] T037 [US3] Implement McpClient wrapper: connect, listTools, callTool, listResources, readResource, listPrompts, getPrompt, disconnect — bridging to C# SDK's McpClient in `src/FsMcp.Client/McpClient.fs`
- [ ] T038 [US3] Implement FsMcp.Client.Async module with Async wrappers for all client functions in `src/FsMcp.Client/McpClient.fs`
- [ ] T039 [US3] Verify T034, T035 tests pass — client connects, lists, calls, returns typed results

**Checkpoint**: US3 complete — full client wrapper with typed F# API. Independently testable.

---

## Phase 6: User Story 4 — Extend with Custom Middleware (Priority: P4)

**Goal**: Developer can compose middleware functions for cross-cutting concerns on the server

**Independent Test**: Define a logging middleware, attach to server, invoke tool, verify middleware executed

### Tests for User Story 4

- [ ] T040 [P] [US4] Write Expecto tests for middleware composition (compose, pipeline) in `tests/FsMcp.Server.Tests/MiddlewareTests.fs`
- [ ] T041 [P] [US4] Write Expecto tests for middleware execution order and request rejection in `tests/FsMcp.Server.Tests/MiddlewareTests.fs`

### Implementation for User Story 4

- [ ] T042 [US4] Implement McpContext, McpResponse, McpMiddleware types in `src/FsMcp.Server/Middleware.fs`
- [ ] T043 [US4] Implement Middleware.compose and Middleware.pipeline functions in `src/FsMcp.Server/Middleware.fs`
- [ ] T044 [US4] Integrate middleware pipeline into Server.run (execute middleware chain before handlers) in `src/FsMcp.Server/Transport.fs`
- [ ] T045 [US4] Add `middleware` keyword to McpServerBuilder CE in `src/FsMcp.Server/ServerBuilder.fs`
- [ ] T046 [US4] Verify T040, T041 tests pass — middleware composes, executes in order, can reject requests

**Checkpoint**: US4 complete — middleware pipeline works. Independently testable.

---

## Phase 7: User Story 5 — Comprehensive Test Suite as Documentation (Priority: P5)

**Goal**: Every public function tested, every domain type has FsCheck generators, every extension point demonstrated

**Independent Test**: Run full test suite — all pass; audit confirms 100% public function coverage

### Tests for User Story 5

- [ ] T047 [US5] Audit all public functions in FsMcp.Core — add missing Expecto tests to `tests/FsMcp.Core.Tests/`
- [ ] T048 [P] [US5] Audit all public functions in FsMcp.Server — add missing Expecto tests to `tests/FsMcp.Server.Tests/`
- [ ] T049 [P] [US5] Audit all public functions in FsMcp.Client — add missing Expecto tests to `tests/FsMcp.Client.Tests/`
- [ ] T050 [US5] Write example tests demonstrating middleware usage (logging, auth rejection) in `tests/FsMcp.Server.Tests/MiddlewareExampleTests.fs`
- [ ] T051 [US5] Write example tests demonstrating custom tool/resource/prompt handler registration in `tests/FsMcp.Server.Tests/HandlerExampleTests.fs`
- [ ] T052 [US5] Verify FsCheck shrinking produces minimal counterexamples for all domain type generators in `tests/FsMcp.Core.Tests/PropertyTests.fs`
- [ ] T053 [US5] Verify all tests pass via `dotnet test` with zero failures

**Checkpoint**: US5 complete — test suite is comprehensive living documentation

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Quality improvements across all projects

- [ ] T054 [P] Validate quickstart.md scenario end-to-end (define server, run, connect client, invoke tool)
- [ ] T055 [P] Verify no `obj` or `dynamic` in any public API surface across all three projects
- [ ] T056 Run full `dotnet test` suite — confirm all tests pass, FsCheck runs 100+ cases per property
- [ ] T057 Verify solution builds cleanly with zero warnings on `dotnet build`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **US1 Server (Phase 3)**: Depends on Foundational
- **US2 Types Deep Coverage (Phase 4)**: Depends on Foundational; can run in parallel with US1
- **US3 Client (Phase 5)**: Depends on Foundational; can run in parallel with US1 and US2
- **US4 Middleware (Phase 6)**: Depends on US1 (server must exist)
- **US5 Test Audit (Phase 7)**: Depends on US1, US2, US3, US4 (all code must exist)
- **Polish (Phase 8)**: Depends on all user stories complete

### User Story Dependencies

- **US1 (P1 Server)**: Depends on Foundational only — no other story dependency
- **US2 (P2 Types)**: Depends on Foundational only — can parallel with US1
- **US3 (P3 Client)**: Depends on Foundational only — can parallel with US1 and US2
- **US4 (P4 Middleware)**: Depends on US1 (server builder must exist for integration)
- **US5 (P5 Test Audit)**: Depends on all prior stories (audits all code)

### Within Each User Story

- Tests MUST be written and FAIL before implementation (Constitution Principle III)
- Types/models before services
- Services before transport/integration
- Core implementation before cross-cutting integration
- Story complete before moving to next priority

### Parallel Opportunities

- T003, T004, T005, T006, T007 (project creation) — all parallel
- T009, T010 (foundational tests) — parallel
- T015, T016, T017 (US1 tests) — parallel
- T025, T026 (generators) — sequential (T026 depends on T025 types)
- T027, T028, T029 (property tests) — T028 and T029 parallel after T027
- T034, T035 (US3 tests) — parallel
- T040, T041 (US4 tests) — parallel
- T047, T048, T049 (US5 audit) — T048 and T049 parallel
- US1, US2, US3 (Phases 3, 4, 5) — can run in parallel after Foundational

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 (Server)
4. **STOP and VALIDATE**: Define a server with one tool, start it, connect with a client
5. Deploy/demo if ready — this is a usable MCP server toolkit

### Incremental Delivery

1. Setup + Foundational → core types exist, tests green
2. Add US1 (Server) → test independently → usable MVP
3. Add US2 (Types deep coverage) → FsCheck property tests prove correctness
4. Add US3 (Client) → test independently → full toolkit
5. Add US4 (Middleware) → test independently → extensible toolkit
6. Add US5 (Test audit) → confirm 100% coverage → production-ready
7. Polish → clean build, quickstart validated

### Parallel Team Strategy

With multiple developers after Foundational is complete:

- Developer A: US1 (Server)
- Developer B: US2 (Types deep coverage) + US3 (Client)
- Merge → Developer A: US4 (Middleware), Developer B: US5 (Test audit)
- Together: Polish

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story is independently completable and testable
- Verify tests fail before implementing (Constitution Principle III)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
