<!--
  Sync Impact Report
  ==================
  Version change: 1.0.0 → 1.1.0
  Modified principles: N/A
  Added sections:
    - Core Principles (7 principles)
    - Technology Stack
    - Development Workflow
    - Governance
  Added rules:
    - Development Workflow: no Co-Authored-By in commits
  Removed sections: N/A
  Templates requiring updates:
    - .specify/templates/plan-template.md — ✅ compatible (Constitution Check section present)
    - .specify/templates/spec-template.md — ✅ compatible (testing scenarios align)
    - .specify/templates/tasks-template.md — ✅ compatible (test-first phasing present)
  Follow-up TODOs: none
-->

# FsMcp Constitution

## Core Principles

### I. Microsoft MCP Foundation

All functionality MUST be built as idiomatic F# wrappers over the
official `ModelContextProtocol` .NET SDK (NuGet: `ModelContextProtocol`).
Protocol-level concerns (transport, JSON-RPC, capability negotiation)
MUST NOT be reimplemented. The toolkit's value is the F# developer
experience on top of a battle-tested C# foundation.

- Direct protocol reimplementation is forbidden unless the upstream
  SDK provably cannot support a required scenario.
- Upstream SDK version MUST be pinned explicitly in the project file.
- Breaking changes in the upstream SDK MUST be absorbed in the wrapper
  layer, not leaked to consumers.

### II. Idiomatic F#

The public API MUST feel native to F# developers.

- Discriminated unions over class hierarchies for domain modelling.
- `Result<'T, 'E>` and `Option<'T>` over exceptions for expected
  failure paths.
- Computation expressions for DSL-style server/tool/resource
  definitions where they reduce boilerplate.
- Pipe-friendly function signatures (`'input -> 'output`).
- Immutable data by default; mutable state MUST be justified.
- `Async<'T>` or `Task<'T>` for all I/O; no synchronous blocking.

### III. Test-First (NON-NEGOTIABLE)

Every module MUST have an accompanying Expecto test module before
or at the time of implementation. Code without tests MUST NOT be
merged.

- Test framework: **Expecto** (no xUnit/NUnit/MSTest).
- Red-Green-Refactor cycle is mandatory: tests MUST fail before
  implementation makes them pass.
- Test names MUST describe the behaviour under test, not the
  function name (e.g., `"returns error when tool name is empty"`).
- Test modules live in a dedicated test project mirroring the
  source project structure.

### IV. Property-Based Testing with FsCheck

Every pure function and data transformation MUST have FsCheck
property-based tests in addition to example-based Expecto tests.

- Custom FsCheck `Arbitrary` generators MUST be written for all
  domain types (tool definitions, resource descriptors, prompts,
  MCP messages).
- Properties to cover at minimum:
  - **Roundtrip**: serialize → deserialize = identity.
  - **Invariants**: domain rules hold for all generated inputs.
  - **Idempotency**: where applicable, `f(f(x)) = f(x)`.
  - **Commutativity/associativity**: where algebraic laws apply.
- FsCheck shrinking MUST be verified to produce minimal
  counterexamples for domain types.
- Minimum 100 test cases per property (FsCheck default); increase
  for critical paths.

### V. Extensibility

The toolkit MUST be open for extension without requiring
modification of core modules.

- Tool, resource, and prompt handlers MUST be registerable via
  composition (functions / interfaces), not inheritance.
- Middleware pipeline for request/response interception MUST be
  supported.
- Users MUST be able to swap serialization, logging, and transport
  layers without forking the library.
- Extension points MUST be documented with at least one example
  in tests.

### VI. Type Safety

Leverage the F# type system to make illegal states
unrepresentable.

- Use single-case DUs for identifiers (`ToolName`, `ResourceUri`)
  instead of raw strings.
- Smart constructors with validation MUST guard domain type
  creation (return `Result` on invalid input).
- Phantom types or measure types where they prevent misuse at
  zero runtime cost.
- No `obj` or `dynamic` in the public API surface.

### VII. Simplicity

Start with the simplest correct solution. Complexity MUST be
earned, not assumed.

- YAGNI: do not build extension points for hypothetical futures
  not covered by current requirements.
- Three similar lines are better than one premature abstraction.
- No wrapper types that add no validation or semantics.
- Prefer `module` + `let` functions over classes unless OOP
  interop with the C# SDK demands it.

## Technology Stack

- **Language**: F# (.NET 8+)
- **MCP Foundation**: `ModelContextProtocol` NuGet package
  (official .NET SDK from `modelcontextprotocol/csharp-sdk`)
- **Test Framework**: Expecto
- **Property Testing**: FsCheck (integrated via
  `Expecto.FsCheck`)
- **Build**: `dotnet` CLI / MSBuild (`.fsproj`)
- **CI Testing**: `dotnet test` with Expecto CLI adapter
- **Serialization**: `System.Text.Json` (aligned with upstream
  SDK)

## Development Workflow

- Every PR MUST include tests for all new/changed public
  functions.
- Property-based tests MUST accompany any new domain type or
  pure transformation.
- `dotnet test` MUST pass with zero failures before merge.
- Test coverage gaps MUST be flagged in code review; reviewers
  MUST verify FsCheck generators exist for new types.
- Commits SHOULD be atomic: one logical change per commit.
- Commit messages MUST NOT contain `Co-Authored-By` or any other
  co-author trailers. All commits are authored solely by the
  committer.
- Prefer small, focused PRs over large batches.

## Governance

This constitution is the highest-authority document for FsMcp.
It supersedes ad-hoc decisions, PR comments, and verbal
agreements.

- **Amendments**: Any change to a principle requires a PR with
  rationale. Breaking changes to principles bump the MAJOR
  version. New principles or material expansions bump MINOR.
  Clarifications bump PATCH.
- **Compliance**: Every PR review MUST include a constitution
  compliance check (see plan template's "Constitution Check"
  section).
- **Versioning**: This document follows semantic versioning
  (MAJOR.MINOR.PATCH).
- **Dispute resolution**: If a principle conflicts with a
  pragmatic need, document the exception with justification in
  the PR description and get explicit approval.

**Version**: 1.1.0 | **Ratified**: 2026-04-02 | **Last Amended**: 2026-04-02
