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
  echo "Generating lock: $project"
  dotnet restore "$project" --use-lock-file --force-evaluate \
    --maxcpucount:1 \
    -nodeReuse:false \
    -p:BuildInParallel=false \
    -p:UseSharedCompilation=false
done

mapfile -t locks < <(find src tests examples -type f -name packages.lock.json -print | LC_ALL=C sort)
if [[ ${#locks[@]} -ne 17 ]]; then
  echo "Expected 17 package lock files, found ${#locks[@]}." >&2
  exit 1
fi
