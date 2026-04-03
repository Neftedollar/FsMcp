# Changelog

All notable changes to FsMcp are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/).

## [1.0.0] - 2026-04-03

### Added

**FsMcp.Core**
- Domain types: `Content`, `ResourceContents`, `McpRole`, `McpMessage`, `McpError`
- Identifier types with smart constructors: `ToolName`, `ResourceUri`, `PromptName`, `MimeType`, `ServerName`, `ServerVersion`
- `ValidationError` DU for structured error reporting
- JSON serialization with custom converters for DUs (MCP wire format)
- Internal `Interop` module for F# <-> C# SDK type conversion

**FsMcp.Server**
- `mcpServer { }` computation expression for declarative server definition
- `Tool.define`, `Resource.define`, `Prompt.define` convenience functions
- `TypedTool.define<'T>` with TypeShape-powered JSON Schema generation and caching
- `mcpTool { }` nested CE for cleaner tool definitions
- `StreamingTool.define` for `IAsyncEnumerable<Content>` handlers
- `ContextualTool.define<'T>` with notification support (progress + logging)
- `Middleware.compose` and `Middleware.pipeline` for composable middleware
- `ValidationMiddleware` — auto-validates tool args against JSON Schema
- `Telemetry.tracing()` — Activity-based spans (OpenTelemetry compatible)
- `Telemetry.MetricsCollector` — request counts and durations
- `DynamicServer` — add/remove tools at runtime with change events
- `Server.run` (stdio) and `Server.runHttp` (HTTP/SSE via ASP.NET Core)

**FsMcp.Client**
- Typed client: `McpClient.connect`, `callTool`, `listTools`, `readResource`, `getPrompt`, `disconnect`
- `ClientTransport.stdio`, `http`, `httpWithHeaders`
- `McpClientAsync` module with `Async<'T>` wrappers

**FsMcp.Testing**
- `TestServer.callTool/readResource/getPrompt` — direct handler invocation
- `Expect.mcpHasTextContent`, `mcpIsError`, `mcpIsSuccess`, `mcpHasContentCount`
- `McpArbitraries` — FsCheck generators for all domain types

**FsMcp.TaskApi**
- `ClientPipeline` with `taskResult { }` CE via FsToolkit.ErrorHandling
- Pipe-friendly: `client |> ClientPipeline.callToolText "name" args`

**FsMcp.Sampling**
- `SamplingRequest` builders: `simple`, `withSystem`, `withTemperature`, `withModel`
- `SamplingTool.define<'T>` — tools that invoke client LLM
- `mockSample` and `noOpSample` for testing

**Examples**
- EchoServer — echo + reverse tools, resource, prompt
- Calculator — add/subtract/multiply/divide
- FileServer — read_file, list_directory, file_info

**Infrastructure**
- 308 Expecto + FsCheck tests across 6 projects
- NuGet packaging for all 6 libraries
- README with architecture diagrams
