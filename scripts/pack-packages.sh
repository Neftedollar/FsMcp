#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-$repo_root/nupkgs}"
repository_commit="${2:-${GITHUB_SHA:-$(git -C "$repo_root" rev-parse HEAD)}}"

if [[ ! "$repository_commit" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "A full 40-character repository commit SHA is required." >&2
  exit 1
fi

packages=(
  src/FsMcp.Client/FsMcp.Client.fsproj
  src/FsMcp.Core/FsMcp.Core.fsproj
  src/FsMcp.Sampling/FsMcp.Sampling.fsproj
  src/FsMcp.Server.Http/FsMcp.Server.Http.fsproj
  src/FsMcp.Server/FsMcp.Server.fsproj
  src/FsMcp.TaskApi/FsMcp.TaskApi.fsproj
  src/FsMcp.Testing/FsMcp.Testing.fsproj
)

mkdir -p "$output"
for project in "${packages[@]}"; do
  dotnet pack "$repo_root/$project" \
    --configuration Release \
    --no-build \
    --no-restore \
    --output "$output" \
    --maxcpucount:1 \
    -nodeReuse:false \
    -p:BuildInParallel=false \
    -p:UseSharedCompilation=false \
    -p:RepositoryCommit="$repository_commit" \
    -p:SourceRevisionId="$repository_commit"
done

count="$(find "$output" -maxdepth 1 -type f -name 'FsMcp.*.nupkg' ! -name '*.snupkg' | wc -l)"
if [[ "$count" -ne 7 ]]; then
  echo "Expected exactly seven FsMcp packages, found $count." >&2
  exit 1
fi
