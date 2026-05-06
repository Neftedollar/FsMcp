#!/usr/bin/env bash
# Snapshot memory state of MCP servers WITHOUT triggering GC heap-walks.
#
# Background: dotnet-gcdump on macOS has been observed to kill .NET processes
# (EventPipe heap-walk + slow diagnostic port → process aborts).
# This script avoids gcdump entirely and uses only kernel-side / counter-side data:
#   - ps           kernel: RSS, VSZ, %CPU, ETIME
#   - vmmap        macOS: per-region resident sizes (read-only mach VM probe)
#   - lsof         kernel: open file descriptor count
#   - top -l 1     kernel: detailed memory stats including dirty/private
#   - dotnet-counters collect  EventPipe counters only, no heap walk (safe)
#
# Usage:
#   ./leak-snapshot.sh <label>                        # snapshot EchoServer (default)
#   PROC_PATTERN='fslangmcp$' ./leak-snapshot.sh tag  # snapshot a specific process
#   ./leak-snapshot.sh --all <label>                  # snapshot every known target
#   ./leak-snapshot.sh --diff <label-a> <label-b>     # quick textual diff

# Note: intentionally NOT using `set -e` — the snapshot pipeline tolerates
# individual command failures (e.g. SIGPIPE from `head` or vmmap permission
# issues) and should still complete the second process snapshot.
set -uo pipefail

LEAK_DIR="${LEAK_DIR:-$HOME/leak-runs/$(date +%Y%m%d)}"
PROC_PATTERN="${PROC_PATTERN:-EchoServer.dll}"
COUNTERS_DURATION="${COUNTERS_DURATION:-5}"  # seconds; counters collect window
COUNTERS_BIN="${COUNTERS_BIN:-$HOME/.dotnet/tools/dotnet-counters}"

mkdir -p "$LEAK_DIR"

snapshot_one() {
  local label="$1"
  local pattern="$2"
  local pid
  pid=$(PROC_PATTERN="$pattern" pgrep -f "$pattern" | head -1 || true)
  if [ -z "$pid" ]; then
    echo "[$label] no process matching '$pattern' — skipping"
    return 0
  fi
  local stamp
  stamp=$(date +%H%M%S)
  local base="$LEAK_DIR/${label}-pid${pid}-${stamp}"

  # ps — RSS/VSZ/ETIME from kernel, never touches the process.
  ps -o pid,rss,vsz,pcpu,etime,command -p "$pid" > "$base.ps.txt"

  # vmmap — read-only VM probe; safe under normal circumstances.
  # Suppressed errors when not codesigned for task_for_pid.
  vmmap "$pid" > "$base.vmmap.txt" 2>/dev/null || true

  # lsof — kernel-side FD enumeration. Useful for FD leaks.
  local fd_count
  fd_count=$(lsof -p "$pid" 2>/dev/null | tail -n +2 | wc -l | tr -d ' ')
  echo "$fd_count" > "$base.fdcount.txt"

  # top -l 1 — single sample, kernel-side; gives faults/pageins/cow if interesting.
  top -l 1 -pid "$pid" -stats pid,rsize,vsize,vprvt,faults,pageins,cow,threads,instrs > "$base.top.txt" 2>/dev/null || true

  # NOTE: dotnet-counters intentionally NOT used here.
  # Two reasons:
  #   1. macOS .NET diagnostic port is flaky — concurrent counter sessions
  #      leave stale processes that block the next attempt.
  #   2. RSS (ps) and VM_ALLOCATE region size (vmmap) give the same
  #      "is managed heap growing" signal without any diagnostic-port traffic.
  # If you ever need heap-stat by type, take ONE careful gcdump manually,
  # not in a loop.

  # Distill summary line for fast scanning.
  local summary="$LEAK_DIR/${label}.summary.txt"
  awk -v label="$label" -v pid="$pid" -v fd="$fd_count" 'NR==2 {
    printf "%s pid=%s rss=%.1fMB vsz=%.1fMB cpu=%s%% etime=%s fd=%s\n",
      label, pid, $2/1024, $3/1024, $4, $5, fd
  }' "$base.ps.txt" | tee "$summary" >&2

  # vmmap top-3 dynamic regions: VM_ALLOCATE / mapped file / MALLOC totals.
  awk '/^VM_ALLOCATE / || /^mapped file / || /^MALLOC_SMALL / || /^TOTAL / { print "  " $0 }' \
    "$base.vmmap.txt" 2>/dev/null | head -8 >&2

}

snapshot() {
  snapshot_one "$1" "$PROC_PATTERN"
}

snapshot_all() {
  local label="$1"
  snapshot_one "${label}-echo"    'EchoServer.dll'
  snapshot_one "${label}-fslang"  'fslangmcp'
  # fsautocomplete is a child of fslangmcp; it has its own .NET process
  # and its own FCS state, so we have to track it separately.
  # Pattern '^fsautocomplete$' matches its full command line exactly,
  # avoiding self-match by the pgrep wrapper.
  snapshot_one "${label}-fsac"    '^fsautocomplete$'
  # Other FsMcp-based MCP servers the user runs alongside.
  # Each is a separate .NET process; if multiple instances exist
  # (one per parent — claude, codex, etc.), pgrep | head -1 picks one;
  # rerun with PROC_PATTERN= to target a specific PID.
  snapshot_one "${label}-agemcp"  'tools/age-mcp'
  snapshot_one "${label}-obrien"  'tools/obrien-mcp'
  snapshot_one "${label}-ageflow" 'agents-workflow.*mcp-server|serve:auto'
}

diff_snapshots() {
  local a="$1" b="$2"
  local fa fb
  fa=$(ls -1 "$LEAK_DIR/$a"-*.ps.txt 2>/dev/null | head -1 || true)
  fb=$(ls -1 "$LEAK_DIR/$b"-*.ps.txt 2>/dev/null | head -1 || true)
  if [ -z "$fa" ] || [ -z "$fb" ]; then
    echo "Could not find ps snapshots for '$a' or '$b' in $LEAK_DIR"
    exit 1
  fi
  local tmp_a tmp_b
  tmp_a=$(mktemp)
  tmp_b=$(mktemp)
  trap 'rm -f "$tmp_a" "$tmp_b"' RETURN
  echo "=== $a -> $b ==="
  awk -v which=A 'NR==2 {printf "%s rss=%.1fMB vsz=%.1fMB cpu=%s etime=%s\n", which, $2/1024, $3/1024, $4, $5}' "$fa"
  awk -v which=B 'NR==2 {printf "%s rss=%.1fMB vsz=%.1fMB cpu=%s etime=%s\n", which, $2/1024, $3/1024, $4, $5}' "$fb"
  awk 'NR==2 {a_rss=$2; a_vsz=$3} END {print "rss_kb_A="a_rss; print "vsz_kb_A="a_vsz}' "$fa" > "$tmp_a"
  awk 'NR==2 {b_rss=$2; b_vsz=$3} END {print "rss_kb_B="b_rss; print "vsz_kb_B="b_vsz}' "$fb" > "$tmp_b"
  local arss brss avsz bvsz
  arss=$(awk -F= '/rss/{print $2}' "$tmp_a")
  brss=$(awk -F= '/rss/{print $2}' "$tmp_b")
  avsz=$(awk -F= '/vsz/{print $2}' "$tmp_a")
  bvsz=$(awk -F= '/vsz/{print $2}' "$tmp_b")
  awk -v a="$arss" -v b="$brss" -v av="$avsz" -v bv="$bvsz" 'BEGIN {
    printf "delta_rss=%+0.1fMB  delta_vsz=%+0.1fMB\n", (b-a)/1024, (bv-av)/1024
  }'
}

case "${1:-}" in
  --all)
    [ $# -eq 2 ] || { echo "Usage: $0 --all <label>"; exit 1; }
    snapshot_all "$2"
    ;;
  --diff)
    [ $# -eq 3 ] || { echo "Usage: $0 --diff <label-a> <label-b>"; exit 1; }
    diff_snapshots "$2" "$3"
    ;;
  "")
    echo "Usage: $0 <label>                  # snapshot one process (PROC_PATTERN env)"
    echo "       $0 --all <label>            # snapshot every known target"
    echo "       $0 --diff <a> <b>           # textual diff"
    exit 1
    ;;
  *)
    snapshot "$1"
    ;;
esac
