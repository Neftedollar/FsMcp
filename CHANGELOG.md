# Changelog

All notable changes to FsMcp are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/).

## [2.0.0] - 2026-08-15

### Added

- **Enterprise-managed authorization for MCP clients.** `FsMcp.Client` now
  exposes opaque, validated F# configuration for the stable MCP ID-JAG profile,
  RFC 8693 token exchange with RFC 7523 identity assertions, bounded
  single-flight token caching, caller cancellation, redacted diagnostics, and
  one controlled refresh/replay after an authentication challenge.
- **Secure server composition.** Stdio and Streamable HTTP servers can be
  registered into caller-owned service collections/builders so applications can
  use official SDK request filters and ASP.NET Core authentication and
  authorization around `MapMcp`.
- Cancellation-aware client operations and server handlers, plus `Async`
  wrappers that carry the ambient F# cancellation token.
- Stateful Streamable HTTP resource subscriptions with bounded fan-out and
  disconnect cleanup. Stateless HTTP omits the subscribe capability and rejects
  subscribe requests.

### Changed

- **Breaking:** tool, resource, prompt, typed, contextual, sampling, and
  streaming handler signatures now receive `CancellationToken` as their final
  argument. Request cancellation is preserved instead of being converted to a
  protocol or transport failure.
- **Breaking:** `McpClient.readResource` now returns the complete
  `ResourceContents list`; 1.x silently discarded every item after the first.
  Prompt discovery now exposes declared argument metadata.
- **Breaking:** resource subscriptions use opaque `SubscriptionId` and
  `SubscriptionError` values. `subscribe` validates input and returns a
  `Result`; notification functions accept their registry last for pipeline use.
- Typed tool input is strict JSON. JSON strings such as `"42"` and `"true"`
  are no longer coerced to numeric or Boolean fields and fail with `-32602`.
  String coercion remains available for URI-template and prompt arguments.
- Package production is reproducible: .NET SDK 10.0.400, MCP SDK 1.4.1,
  Microsoft.Extensions 10.0.7, exact project lock files, SHA-pinned Actions,
  vulnerability audits, exact package-set metadata checks, and clean C#/F#
  consumer builds. Packages publish an `FSharp.Core` floor of 10.1.203 rather
  than an upper-bounded exact dependency.

### Fixed

- Resource and prompt handlers now receive protocol arguments, server
  name/version metadata is preserved exactly, handler failures become protocol
  errors, and exception details no longer expose secrets.
- Client cancellation, disconnect, process cleanup, and shared disconnect
  outcomes are deterministic; the client stdio path is covered by a real
  self-hosted end-to-end test.
- Streamable HTTP subscription capabilities and behavior now agree in both
  stateful and stateless modes.

### Removed and fail-closed

- **The dead transport selector was removed.** `ServerConfig.Transport` and the
  `useStdio` / `useHttp` computation-expression operations never selected the
  runtime transport. Use `Server.run` for stdio or `FsMcp.Server.Http` for
  Streamable HTTP.
- Legacy FsMcp middleware declarations, dynamic hot-reload, contextual
  notifications, and `SamplingTool.define` no longer pretend to provide runtime
  behavior that the SDK bridge never wired. Compatibility entry points are
  obsolete and reject unsupported use; use SDK request filters, request-scoped
  SDK primitives, or ASP.NET Core middleware directly.

### Enterprise authorization boundaries

- The ready-made flow supports HTTPS endpoints (with explicit loopback HTTP only
  in development), exact metadata issuer/token-endpoint validation, strict
  Bearer challenges, same-origin token attachment, bounded bodies/timeouts, and
  no redirects or HTTPS downgrade.
- The shipped profile uses `client_secret_post` when a secret is configured.
  It does not implement `client_secret_basic`, `private_key_jwt`, SAML assertions,
  DPoP, mTLS sender-constrained tokens, dynamic client registration, or token
  introspection. Configure those requirements outside the ready-made flow.
- With the pinned MCP SDK 1.4.1, authorization-server issuer paths or queries
  are rejected because its RFC 8414 discovery helper cannot preserve them.
  IdP token endpoints may be configured explicitly and are matched exactly.

## [1.2.2] - 2026-08-02

### Fixed

- **Windows startup crash in 1.2.0 and 1.2.1.** The stderr sink introduced in 1.2.0
  opened file descriptor 2 directly to bypass `ConsolePal`'s shared console monitor.
  Descriptor numbers are a Unix concept — on Windows handles are opaque, and
  `SafeFileHandle(nativeint 2)` throws `The handle is invalid` while constructing the
  logger provider. Any Windows host running an FsMcp server with console logging
  enabled (the default) failed at startup.

  The sink is now platform-aware: descriptor 2 on Unix, `Console.OpenStandardError()`
  on Windows. The bypass is only needed on Unix in the first place —
  `ConsolePal.Windows` does not take the process-wide monitor that causes the wedge.

  **Windows users on 1.2.0 or 1.2.1 must upgrade.** Unix users are unaffected.

## [1.2.1] - 2026-08-02

### Fixed

- **Pinned the `FSharp.Core` floor we publish.** The projects relied on the SDK's
  implicit `FSharp.Core` reference, so the dependency written into our `.nuspec`
  drifted with whatever SDK built the release. 1.2.0 shipped a `>= 10.1.302` floor
  that way — an upgrade no consumer asked for, and a hard break for anyone pinned to
  `FSharp.Compiler.Service` 43.12.203, which requires exactly `10.1.203`. The floor
  is now declared explicitly at `10.1.203`, matching 1.1.1.

  Consumers on 1.2.0 that hit `NU1605` / `NU1608` around `FSharp.Core` should move to
  1.2.1; no source changes are needed.

  Note that `DisableImplicitFSharpCoreReference` is required here. `Directory.Build.props`
  is imported before the F# targets that add the implicit reference, so a
  `PackageReference Update` in that file has nothing to act on and the SDK version
  silently wins — verified by inspecting the produced `.nuspec`.

## [1.2.0] - 2026-08-02

### Fixed

- **Stdio servers no longer wedge when the host leaves stderr unread.** A host that
  spawns an MCP server over stdio owns the child's stderr pipe; hosts that never
  read it let the pipe fill at 64 KB, reached after a couple of hundred logged
  requests. The built-in console logger wrote stderr through `ConsolePal`, which
  takes one process-wide monitor shared with stdout — so a write parked on the full
  pipe held that lock and the server's next response to stdout blocked behind it.
  Every caller looked healthy; the server simply went silent while still holding
  live requests.

  Logging now goes through `NonBlockingStderrLoggerProvider`, which opens file
  descriptor 2 directly instead of a `ConsoleStream`, so a stalled stderr write can
  no longer stall stdout. Log lines are handed to a bounded channel in `DropWrite`
  mode (callers never block, never throw) and drained by one dedicated thread.
  Diagnostics are dropped under backpressure; requests are not.

  Measured against `examples/EchoServer` with stderr never drained: **204 replies
  before going silent on 1.1.1, 938,933 after the fix.** Reproduce with
  `scripts/repro-stdio-stderr-wedge.py`.

  Note for anyone who tried the obvious remedy: `ConsoleLoggerOptions.QueueFullMode
  = DropWrite` does **not** help. That setting governs behaviour once the logger's
  queue is full, and the block happens on the console monitor long before the queue
  fills.

### Added

- `consoleLogging` custom operation on the `mcpServer { }` computation expression,
  and a matching `ConsoleLogging` field on `ServerConfig`. Defaults to `true`.
  Set it to `false` when the host discards stderr anyway, so no work is spent
  formatting messages nobody reads.
- `scripts/repro-stdio-stderr-wedge.py` — end-to-end harness for the wedge above.
  It needs a real child process with an undrained stderr pipe, which is why it is a
  script rather than an Expecto test: in-process the runner always drains stderr and
  the defect cannot manifest.

### Changed

- `ServerConfig` gained a `ConsoleLogging` field. Code that builds `ServerConfig`
  through the `mcpServer { }` CE is unaffected; code that constructs the record
  literally must add the field.
- Stdio hosts now start from `ClearProviders()`. `Host.CreateApplicationBuilder`
  registers the built-in console provider by default, and that provider is the one
  that deadlocks, so it has to go even when logging stays enabled.

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
