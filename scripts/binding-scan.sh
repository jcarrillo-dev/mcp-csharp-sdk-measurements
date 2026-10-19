#!/usr/bin/env bash
# Memory scan of the binding prototype: every TRANSPORT x WIPE_JSON combination, twice.
# Run from the repository root after publishing prototypes/binding (see README).
set -euo pipefail
binary=prototypes/binding/out/binding
for transport in sdk bytes; do
  for wipe in 0 1; do
    for run in 1 2; do
      echo "##### TRANSPORT=$transport WIPE_JSON=$wipe run=$run"
      TRANSPORT=$transport WIPE_JSON=$wipe "$binary" scan
    done
  done
done
