# Implementation Plan: F# MCP Toolkit

**Branch**: `001-fsharp-mcp-toolkit` | **Date**: 2026-04-02 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-fsharp-mcp-toolkit/spec.md`

## Summary

Build an idiomatic F# wrapper library over the Microsoft
`ModelContextProtocol` .NET SDK. The toolkit provides: (1) F# domain types
(discriminated unions, smart constructors) for MCP protocol concepts,
(2) a server builder DSL using computation expressions, (3) an F# client
wrapper returning typed results, and (4) a composable middleware pipeline.
Every public function has Expecto tests; every domain type has FsCheck
property tests with custom generators.

## Technical Context

**Language/Version**: F# / .NET 8.0
**Primary Dependencies**:
  - `ModelContextProtocol` (official .NET MCP SDK)
  - `Microsoft.Extensions.Hosting` (server hosting)
  - `Microsoft.Extensions.Logging` (structured logging)
  - `System.Text.Json` (serialization, aligned with upstream SDK)
**Storage**: N/A (library, no persistence)
**Testing**: Expecto + FsCheck (via `Expecto.FsCheck`)
**Target Platform**: .NET 8+ (cross-platform: macOS, Linux, Windows)
**Project Type**: Library (3 NuGet-ready projects)
**Performance Goals**: Wrapper overhead < 5% vs raw C# SDK usage
**Constraints**: No `obj`/`dynamic` in public API; `Task<'T>` primary
**Scale/Scope**: ~15-20 F# modules across 3 projects + 3 test projects

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Microsoft MCP Foundation | ✅ PASS | Wraps `ModelContextProtocol` NuGet; no protocol reimplementation |
| II. Idiomatic F# | ✅ PASS | DUs, Result/Option, CEs, pipe-friendly, Task primary + Async wrappers |
| III. Test-First | ✅ PASS | Expecto test project per source project; red-green-refactor enforced |
| IV. Property-Based Testing | ✅ PASS | FsCheck generators for all domain types; roundtrip + invariant properties |
| V. Extensibility | ✅ PASS | Middleware pipeline, composition-based handler registration |
| VI. Type Safety | ✅ PASS | Single-case DUs for identifiers, smart constructors, no `obj` in API |
| VII. Simplicity | ✅ PASS | Module + let functions; classes only for C# SDK interop |

## Project Structure

### Documentation (this feature)

```text
specs/001-fsharp-mcp-toolkit/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── FsMcp.Core.md
│   ├── FsMcp.Server.md
│   └── FsMcp.Client.md
└── tasks.md
```

### Source Code (repository root)

```text
FsMcp.sln

src/
├── FsMcp.Core/
│   ├── FsMcp.Core.fsproj
│   ├── Types.fs            # DUs: Content, ToolDefinition, ResourceDescriptor, etc.
│   ├── Validation.fs       # Smart constructors, ValidationError
│   ├── Serialization.fs    # System.Text.Json converters for F# types
│   └── Interop.fs          # Conversion to/from C# SDK types
│
├── FsMcp.Server/
│   ├── FsMcp.Server.fsproj
│   ├── ServerBuilder.fs    # CE-based server definition DSL
│   ├── Middleware.fs        # Middleware pipeline types and composition
│   ├── Handlers.fs         # Tool/Resource/Prompt handler registration
│   └── Transport.fs        # Stdio/HTTP transport configuration helpers
│
└── FsMcp.Client/
    ├── FsMcp.Client.fsproj
    ├── McpClient.fs        # F# client wrapper returning typed results
    └── ClientTransport.fs  # Transport creation helpers

tests/
├── FsMcp.Core.Tests/
│   ├── FsMcp.Core.Tests.fsproj
│   ├── Generators.fs       # FsCheck Arbitrary generators for all domain types
│   ├── TypesTests.fs       # Example-based Expecto tests for types
│   ├── PropertyTests.fs    # FsCheck property tests (roundtrip, invariants)
│   ├── ValidationTests.fs  # Smart constructor tests
│   └── SerializationTests.fs
│
├── FsMcp.Server.Tests/
│   ├── FsMcp.Server.Tests.fsproj
│   ├── ServerBuilderTests.fs
│   ├── MiddlewareTests.fs
│   └── HandlersTests.fs
│
└── FsMcp.Client.Tests/
    ├── FsMcp.Client.Tests.fsproj
    └── McpClientTests.fs
```

**Structure Decision**: Three-project split (`FsMcp.Core`, `FsMcp.Server`,
`FsMcp.Client`) as decided in clarification. Each has a mirrored test
project. `FsMcp.Core` is a dependency of both Server and Client. Solution
file at root.

## Complexity Tracking

No constitution violations to justify.
