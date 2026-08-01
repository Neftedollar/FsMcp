#!/usr/bin/env python3
"""
End-to-end regression harness for the stdio stderr wedge fixed in FsMcp 1.2.0.

WHAT IT REPRODUCES
------------------
An MCP host that spawns a server over stdio owns the child's stderr pipe. Hosts
that never read it let the pipe fill — 64 KB on macOS and Linux, reached after a
couple of hundred logged requests.

Before 1.2.0 that silently killed the server. The console logger wrote stderr
through ConsolePal, which takes ONE process-wide monitor shared with stdout, so a
write parked on the full pipe held the lock and the next response to stdout blocked
behind it. Every caller looked healthy; the server just stopped answering.

    writer thread   ConsolePal.WriteFromConsoleStream   <- holds the lock
                    Interop.Sys.Write                   <- parked, pipe full

    SDK response    ConsoleStream.Write
                    ConsolePal.WriteFromConsoleStream
                    Monitor.Enter_Slowpath              <- waits on that lock

WHY IT IS NOT AN xUnit/Expecto TEST
-----------------------------------
It needs a real child process whose stderr is a pipe nobody drains. In-process the
test runner always drains stderr, so the lock is never held long enough to matter
and the bug cannot manifest.

USAGE
-----
    dotnet build examples/EchoServer
    python3 scripts/repro-stdio-stderr-wedge.py

Exit code 0 = server stayed responsive (fixed). Exit code 1 = wedged (regressed).

Measured on the fix commit: 938,933 replies. On 1.1.1: 204.
"""
import json
import os
import subprocess
import sys
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(REPO, "examples", "EchoServer", "bin", "Debug", "net10.0", "EchoServer")
DURATION = 30
REPLY_TIMEOUT = 15.0

if not os.path.exists(BIN):
    print(f"build the example first:  dotnet build {os.path.join(REPO, 'examples/EchoServer')}")
    sys.exit(2)

read_fd, write_fd = os.pipe()          # read_fd is deliberately never read
proc = subprocess.Popen([BIN], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=write_fd)
os.close(write_fd)

state = {"replies": 0, "completed": False}


def drive():
    def send(payload):
        proc.stdin.write((json.dumps(payload) + "\n").encode())
        proc.stdin.flush()

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                     "clientInfo": {"name": "wedge-repro", "version": "1"}}})
    if not proc.stdout.readline():
        return
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    started = time.time()
    n = 0
    while time.time() - started < DURATION:
        n += 1
        send({"jsonrpc": "2.0", "id": 100 + n, "method": "tools/list", "params": {}})
        if not proc.stdout.readline():
            return
        state["replies"] = n
    state["completed"] = True


worker = threading.Thread(target=drive, daemon=True)
worker.start()
worker.join(timeout=DURATION + REPLY_TIMEOUT)
still_waiting = worker.is_alive()

try:
    proc.kill()
    proc.wait(timeout=5)
except Exception:
    pass
os.close(read_fd)

print(f"replies answered with stderr never drained: {state['replies']}")
if state["completed"] and not still_waiting:
    print("PASS — server stayed responsive for the whole run.")
    sys.exit(0)

print(f"FAIL — server went silent after {state['replies']} replies (wedge regressed).")
sys.exit(1)
