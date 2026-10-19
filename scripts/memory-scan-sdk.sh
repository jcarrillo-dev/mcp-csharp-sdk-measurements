#!/usr/bin/env bash
# Memory scan with the SDK's own stdio transport, built from several refs of a csharp-sdk clone (default: main and the
# two branches that change the read path). Each ref is checked out in a temporary git worktree, so the clone itself is
# not modified, and the prototype is published with Native AOT against it.
# Usage, from the repository root: scripts/memory-scan-sdk.sh /path/to/csharp-sdk [ref...]
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
  name=${ref//\//-}
  dir="$work/$name"
  git -C "$sdk" worktree add --detach --quiet "$dir" "$ref"
  out="$work/out-$name"
  dotnet publish prototypes/memory-scan -c Release -r linux-x64 -p:McpSdkRoot="$dir" -o "$out" > "$work/publish-$name.log" 2>&1 \
    || { cat "$work/publish-$name.log"; exit 1; }
  grep -E "Warn|warn" "$work/publish-$name.log" || true
  for run in 1 2; do
    echo "##### $ref ($(git -C "$dir" rev-parse --short HEAD)) SDK transport, all variants, run=$run"
    "$out/memory-scan"
  done
done
