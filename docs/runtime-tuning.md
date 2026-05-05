---
title: Runtime Tuning for Stdio Servers
category: Guides
categoryindex: 1
index: 0
---

# Runtime Tuning for Stdio Servers

.NET's default Server GC is optimized for throughput. On a busy server that is constantly allocating, this is the right trade-off. For a stdio MCP server that sits idle between requests, the same defaults produce RSS growth that looks alarming but is not a memory leak.

This page explains what is happening, how to confirm it, and which environment variables to set.

## Why default Server GC behaves this way

.NET's Server GC divides the heap across all CPU cores to maximize allocation throughput. When demand grows, it commits more virtual address space ("commit-grow") to give each heap generation room to expand. The key point: **Server GC does not proactively release committed memory back to the OS once the application is idle.** It waits until the operating system signals memory pressure before compacting heaps and returning pages. On a development laptop with tens of gigabytes of RAM, that OS-level signal rarely fires during a quiet session. The result is that a process that allocated 300 MB during a busy period continues to report ~300 MB RSS hours later, even though only a fraction of that memory is genuinely in use. When memory pressure is finally applied externally, the runtime drops committed pages almost instantly — without a restart and without subsequent regrowth.

## Recommended environment variables

### `DOTNET_gcServer=0` — Switch to Workstation GC (recommended for stdio MCP servers)

```bash
DOTNET_gcServer=0
```

Workstation GC uses a single heap regardless of CPU count, and — unlike Server GC — it is designed to return unused committed memory to the OS promptly when the application is idle. For a stdio MCP server, which is mostly idle between tool calls, this is the right default. The throughput penalty is invisible at low request rates.

### `DOTNET_GCHeapHardLimitPercent=10` — Cap committed heap at a fraction of system RAM

```bash
DOTNET_GCHeapHardLimitPercent=10
```

This limits the managed heap to 10% of physical RAM. It works with both Server GC and Workstation GC. On a 32 GB machine, the cap is ~3.2 GB. The runtime triggers a full GC instead of committing beyond the limit, which prevents unbounded RSS growth while keeping Server GC semantics if you prefer them.

### `DOTNET_gcConcurrent=1` — Enable concurrent GC (combine with either option above)

```bash
DOTNET_gcConcurrent=1
```

Concurrent GC performs most of its work alongside your application threads, reducing pause times. It is enabled by default for Workstation GC but worth making explicit if you are combining with `DOTNET_GCHeapHardLimitPercent`. The flag has no effect when Server GC is active — Server GC uses its own background-collection model.

### Recommended baseline for stdio MCP servers

```bash
DOTNET_gcServer=0
DOTNET_gcConcurrent=1
```

## How to set these in MCP client configs

### Claude Code (`~/.claude.json` or per-project `.mcp.json`)

In `~/.claude.json`, each server entry in `mcpServers` accepts an `env` map:

```json
{
  "mcpServers": {
    "my-server": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/MyServer"],
      "env": {
        "DOTNET_gcServer": "0",
        "DOTNET_gcConcurrent": "1"
      }
    }
  }
}
```

Per-project config uses the same shape in `.mcp.json` at the project root.

### Codex (`~/.codex/config.toml`)

In a Codex config file, the `[mcp_servers.<name>.env]` table sets environment variables for each server:

```toml
[mcp_servers.my-server]
command = "dotnet"
args = ["run", "--project", "/path/to/MyServer"]

[mcp_servers.my-server.env]
DOTNET_gcServer = "0"
DOTNET_gcConcurrent = "1"
```

### `runtimeconfig.template.json` for redistributable .NET tools

If you publish your server as a self-contained tool (e.g., via `dotnet pack` as a global tool), you can bake the GC settings into the package so users do not need to set them manually. Add a `runtimeconfig.template.json` at the project root:

```json
{
  "configProperties": {
    "System.GC.Server": false,
    "System.GC.Concurrent": true
  }
}
```

These properties map to `DOTNET_gcServer=0` and `DOTNET_gcConcurrent=1` respectively. Environment variables always override `runtimeconfig` values, so operators can still change behavior without recompiling.

## Diagnostic recipe: "Is this actually a leak?"

Run these three steps in about five minutes before concluding that RSS growth is a bug.

### Step 1 — Watch RSS over time with `ps`

```bash
ps -o pid,rss,vsz,etime,command -p <PID>
```

Run this every minute or two. RSS growing continuously while the server handles requests is expected — but RSS staying high after all activity stops, then dropping when pressure is applied, is the commit-grow pattern, not a leak.

### Step 2 — Inspect VM regions with `vmmap` (macOS)

```bash
vmmap <PID> | head -50
```

Look at the `VM_ALLOCATE` line (the managed heap), the `MALLOC_*` lines (native allocator), and `mapped file` (loaded assemblies). If `VM_ALLOCATE` is large but `MALLOC_SMALL` and `mapped file` are stable, the uncommitted managed heap is the source of reported RSS — not an FD leak, not a native allocation leak.

### Step 3 — Trigger memory pressure

On macOS:

```bash
memory_pressure -l critical -s 60
```

Or simply open a memory-heavy application (a browser with many tabs, a VM, Xcode). If the target process's RSS drops by hundreds of megabytes within a minute **without a restart**, the memory was committed-but-not-retained managed heap. Server GC returned it the moment the OS asked. That is not a leak.

A genuine leak does not respond to external pressure: RSS remains elevated or continues growing even after the server has been completely idle for several minutes post-pressure.

The helper script [`scripts/leak-snapshot.sh`](../scripts/leak-snapshot.sh) automates steps 1 and 2, writing timestamped `ps` and `vmmap` files to `~/leak-runs/<date>/` for later comparison:

```bash
# Snapshot the EchoServer process
./scripts/leak-snapshot.sh before

# ... wait a few minutes or run some requests ...

./scripts/leak-snapshot.sh after

# Compare
./scripts/leak-snapshot.sh --diff before after
```

## What `dotnet-gcdump` and `dotnet-dump` do NOT do safely on macOS

If you reach for `dotnet-gcdump collect` or `dotnet-dump collect` to get a heap snapshot, be aware of a macOS-specific hazard: on .NET 10, both tools require the target process to respond on its diagnostic pipe within a timeout. If the pipe handshake is slow (common on a loaded machine, or if another diagnostic session left a stale socket), the tool can trigger an unhandled exception in the target process, terminating it.

Prefer these alternatives:

- **`vmmap <PID>`** — read-only mach VM probe, never touches the process's managed state.
- **`ps -o pid,rss,vsz,etime,command -p <PID>`** — kernel-side, always safe.
- **`dotnet-counters monitor --process-id <PID>`** — EventPipe counters only; does not walk the heap.

If you need a heap-by-type breakdown, take a single `dotnet-gcdump` manually (not in a loop) on a quiescent process, and be prepared to restart the server if it does not respond afterwards.
