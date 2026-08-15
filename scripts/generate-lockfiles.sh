#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

projects=()
while IFS= read -r project; do
  projects+=("$project")
done < <(find src tests examples -type f -name '*.fsproj' -print | LC_ALL=C sort)

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

locks=()
while IFS= read -r lock; do
  locks+=("$lock")
done < <(find src tests examples -type f -name packages.lock.json -print | LC_ALL=C sort)
if [[ ${#locks[@]} -ne 17 ]]; then
  echo "Expected 17 package lock files, found ${#locks[@]}." >&2
  exit 1
fi
