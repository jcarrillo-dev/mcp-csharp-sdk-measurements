#!/usr/bin/env bash
# Binding prototype built against the tool-parameter-binding branch of a csharp-sdk clone (SDK_BRANCH): the binders go
# through McpServerToolCreateOptions.ConfigureParameterBinding and the SDK disposes the bound values, without a filter.
# The branch is checked out in a temporary git worktree, so the clone itself is not modified.
# Usage, from the repository root: scripts/binding-tests-sdk.sh /path/to/csharp-sdk [ref]
set -euo pipefail
sdk=$(cd "$1" && pwd)
ref=${2:-tool-parameter-binding}

work=$(mktemp -d)
trap 'git -C "$sdk" worktree remove --force "$work/sdk" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

git -C "$sdk" worktree add --detach --quiet "$work/sdk" "$ref"
dotnet publish prototypes/binding -c Release -r linux-x64 -p:McpSdkRoot="$work/sdk" -o "$work/out" > "$work/publish.log" 2>&1 \
  || { cat "$work/publish.log"; exit 1; }
grep -E "Warn|warn" "$work/publish.log" || true
echo "##### $ref ($(git -C "$work/sdk" rev-parse --short HEAD))"
"$work/out/binding" tests
