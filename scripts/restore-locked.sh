#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

mapfile -t projects < <(find src tests examples -type f -name '*.fsproj' -print | LC_ALL=C sort)

if [[ ${#projects[@]} -ne 17 ]]; then
  echo "Expected 17 projects, found ${#projects[@]}." >&2
  exit 1
fi

for project in "${projects[@]}"; do
  lock_file="${project%/*}/packages.lock.json"
  if [[ ! -f "$lock_file" ]]; then
    echo "Missing required lock file: $lock_file" >&2
    exit 1
  fi
  echo "Locked restore: $project"
  dotnet restore "$project" --locked-mode \
    --maxcpucount:1 \
    -nodeReuse:false \
    -p:BuildInParallel=false \
    -p:UseSharedCompilation=false
done
