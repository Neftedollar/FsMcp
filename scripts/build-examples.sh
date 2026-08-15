#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

examples=(
  examples/Calculator/Calculator.fsproj
  examples/EchoServer/EchoServer.fsproj
  examples/FileServer/FileServer.fsproj
)

for project in "${examples[@]}"; do
  echo "Building example: $project"
  dotnet build "$project" --configuration Release --no-restore \
    --maxcpucount:1 \
    -nodeReuse:false \
    -p:BuildInParallel=false \
    -p:UseSharedCompilation=false
done
