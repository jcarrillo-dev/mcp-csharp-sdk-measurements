#!/usr/bin/env bash
# Allocations per tools/call for several refs of a csharp-sdk clone (default: main and the two branches that change
# the read path). Each ref is checked out in a temporary git worktree, so the clone itself is not modified.
# Usage, from the repository root: scripts/sdk-allocations.sh /path/to/csharp-sdk [ref...]
set -euo pipefail
sdk=$(cd "$1" && pwd)
shift
refs=("$@")
if [ ${#refs[@]} -eq 0 ]; then
  refs=(main stdio-pipereader toolcall-from-jsonelement)
fi

work=$(mktemp -d)
trap 'for d in "$work"/*/; do git -C "$sdk" worktree remove --force "$d" >/dev/null 2>&1 || true; done; rm -rf "$work"' EXIT

for ref in "${refs[@]}"; do
  dir="$work/${ref//\//-}"
  git -C "$sdk" worktree add --detach --quiet "$dir" "$ref"
  echo "##### $ref ($(git -C "$dir" rev-parse --short HEAD))"
  dotnet run --project sdk-allocations -c Release -p:McpSdkRoot="$dir"
done
