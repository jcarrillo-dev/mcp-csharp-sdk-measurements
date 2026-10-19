#!/usr/bin/env bash
# Allocations per tools/call over Streamable HTTP for several refs of a csharp-sdk clone (default: main, the first
# commit of toolcall-from-jsonelement, which leaves HTTP on the current path, and the branch head, which covers HTTP).
# Each ref is checked out in a temporary git worktree, so the clone itself is not modified.
# Usage, from the repository root: scripts/sdk-allocations-http.sh /path/to/csharp-sdk [ref...]
set -euo pipefail
sdk=$(cd "$1" && pwd)
shift
refs=("$@")
if [ ${#refs[@]} -eq 0 ]; then
  refs=(main toolcall-from-jsonelement~1 toolcall-from-jsonelement)
fi

work=$(mktemp -d)
trap 'for d in "$work"/*/; do git -C "$sdk" worktree remove --force "$d" >/dev/null 2>&1 || true; done; rm -rf "$work"' EXIT

for ref in "${refs[@]}"; do
  dir="$work/$(echo "$ref" | tr '/~^' '---')"
  git -C "$sdk" worktree add --detach --quiet "$dir" "$ref"
  echo "##### $ref ($(git -C "$dir" rev-parse --short HEAD))"
  dotnet run --project sdk-allocations-http -c Release -p:McpSdkRoot="$dir"
done
