# Contributing to FsMcp

Thanks for your interest in FsMcp! Every contribution matters.

## Quick Start

```bash
git clone https://github.com/FsMcp/FsMcp.git
cd FsMcp
dotnet build
dotnet test
```

## Development Rules

These are non-negotiable (see `.specify/memory/constitution.md`):

1. **Tests first** — write failing Expecto tests before implementation
2. **FsCheck** — every pure function and domain type gets property tests
3. **No `obj` in public API** — use typed alternatives
4. **`Task<'T>` primary** — `Async` wrappers in separate modules
5. **Commits in English** — no co-author trailers

## Project Structure

```
src/FsMcp.Core/       — Domain types (start here to understand the codebase)
src/FsMcp.Server/     — Server builder, handlers, middleware
src/FsMcp.Client/     — Client wrapper
src/FsMcp.Testing/    — Test helpers
src/FsMcp.TaskApi/    — FsToolkit.ErrorHandling pipeline
src/FsMcp.Sampling/   — LLM sampling (separate package)
tests/                — Mirror of src/ with test projects
examples/             — Runnable example servers
```

## How to Contribute

### Bug Reports

Open an issue with:
- What you expected
- What happened
- Minimal reproduction code

### Feature Requests

Open an issue describing:
- The use case (what are you building?)
- Why existing APIs don't cover it

### Pull Requests

1. Fork and create a feature branch
2. Write tests first (they must fail before your implementation)
3. Keep PRs focused — one feature per PR
4. Run `dotnet test` — all 308+ tests must pass
5. Update CHANGELOG.md

### Adding a New Module

1. Create `src/FsMcp.YourModule/` with `.fsproj`
2. Create `tests/FsMcp.YourModule.Tests/` with test project
3. Add both to `FsMcp.slnx`
4. Write tests, implement, verify `dotnet test` passes

## Code Style

- F# records and DUs over classes
- `module` + `let` functions over methods
- Pipe-friendly: data-last parameter order
- Meaningful test names: `"returns error when tool name is empty"`

## Questions?

Open a Discussion or issue. We're friendly.
