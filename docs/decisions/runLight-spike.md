# Spike: Server.runLight for lean stdio servers

**Issue:** #3  
**Date:** 2026-05-06  
**Verdict:** Reject — delta below threshold on both metrics

---

## Question

Is `Server.runLight` (a stdio MCP host that bypasses `Host.CreateApplicationBuilder`) worth
shipping in v1.1.0 as a public API alongside the existing `Server.run`?

Acceptance criteria (pre-set in issue #3):

- Adopt if RSS delta > 15 MB **or** startup-time delta > 150 ms vs `Server.run`.
- Reject otherwise.

---

## Implementation

`runLight` was prototyped in `src/FsMcp.Server/Transport.fs` alongside `Server.run`.
It uses a bare `ServiceCollection` + `BuildServiceProvider()` instead of
`Host.CreateApplicationBuilder()`, skipping `IConfiguration`, `ConsoleLifetime`, and the
`IHostedService` orchestration stack. The resulting `McpServer` instance (registered as a
singleton by `AddMcpServer()`) is resolved directly and started with `server.RunAsync()`.

The prototype compiled cleanly against ModelContextProtocol SDK 1.2.0 and produced correct
MCP responses — the `McpServer.RunAsync(CancellationToken)` method is part of the public API.

---

## Measurements

Platform: macOS 15 (Darwin 25.4.0), Apple Silicon, Release build, .NET 10.0.  
Protocol: newline-delimited JSON stdio.  
Workload per run: process launch → `initialize` request → read response (startup time sampled
here) → `tools/list` → read response → `ps -o rss` sample → terminate.

### Server.run (standard — `Host.CreateApplicationBuilder`)

| Run | Startup (ms) | RSS (MB) |
|-----|-------------|---------|
| 1   | 338.6       | 69.1    |
| 2   | 332.2       | 69.2    |
| 3   | 335.5       | 69.3    |
| **avg** | **335.4** | **69.2** |

### Server.runLight (bare `ServiceCollection`)

| Run | Startup (ms) | RSS (MB) |
|-----|-------------|---------|
| 1   | 265.4       | 67.1    |
| 2   | 257.4       | 67.3    |
| 3   | 266.0       | 67.1    |
| **avg** | **262.9** | **67.2** |

### Deltas

| Metric  | Delta    | Threshold | Threshold met? |
|---------|----------|-----------|----------------|
| RSS     | −2.1 MB  | > 15 MB   | **no**         |
| Startup | −72.5 ms | > 150 ms  | **no**         |

---

## Rationale

The `Host.CreateApplicationBuilder` path brings `IConfiguration`, `ConsoleLifetime`, and
`IHostedService` machinery — but at the .NET runtime level the cost is surprisingly low for
a process that shares the same native runtime and CLR startup. The `McpServer` itself
(transport, JSON-RPC loop, options setup) accounts for the vast majority of both RSS and
startup time. Skipping the host saves roughly 72 ms of wall-clock and 2 MB of RSS — real
savings, but insufficient to justify a second public entry point that callers must reason
about.

`Server.run` will remain the single public stdio entry point for v1.1.0.

---

## Recommendation

Close issue #3 as "won't do for v1.1.0". Re-evaluate if the SDK later exposes a
no-reflection, no-DI path (e.g. `McpServer.Create` factory with a custom `ITransport` wired
directly to `StdioServerTransport`), which could change the memory profile more substantially.
