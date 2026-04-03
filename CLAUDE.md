# FsMcp Development Guidelines

## Technologies

- F# / .NET 10.0
- Microsoft ModelContextProtocol SDK 1.2.0
- Expecto + FsCheck (testing)
- System.Text.Json (serialization)

## Project Structure

```
src/FsMcp.Core/       — Domain types, validation, serialization, interop (internal)
src/FsMcp.Server/     — Server builder CE, handlers, middleware, transport (stdio + HTTP)
src/FsMcp.Client/     — Client wrapper, transport helpers, async module
src/FsMcp.Testing/    — Test helpers: assertions, FsCheck generators, test server
tests/                — Mirrored test projects for each library
```

## Commands

```bash
dotnet build          # build all projects
dotnet test           # run 227 Expecto + FsCheck tests
```

## Code Style

- Standard Expecto (NOT Expecto.Flip): `Expect.equal actual expected "message"`
- FsCheck properties: `testPropertyWithConfig config "name" <| fun x -> ...` at testList level
- Smart constructors return `Result<'T, ValidationError>`
- No `obj` or `dynamic` in public API (Constitution Principle VI)
- `Task<'T>` primary for async; `Async<'T>` wrappers in separate modules
- Commits in English, NO Co-Authored-By trailers

## Key Namespaces

- `FsMcp.Core` — types, validation, Content module, serialization
- `FsMcp.Core.Validation` — ToolName, ResourceUri, PromptName, MimeType, etc.
- `FsMcp.Server` — mcpServer CE, Tool/Resource/Prompt.define, Server.run/runHttp
- `FsMcp.Client` — McpClient.connect/callTool/listTools, ClientTransport
- `FsMcp.Testing` — TestServer.callTool, Expect.mcpHasTextContent, McpArbitraries

<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
