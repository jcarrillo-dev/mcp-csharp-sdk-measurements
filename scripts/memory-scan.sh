#!/usr/bin/env bash
# Memory scan of the four ways a tool can receive a secret argument (A to D), with the SDK stdio transport and with
# a transport that reads bytes. Each variant is also run alone, so variants cannot affect each other.
# Run from the repository root after publishing prototypes/memory-scan (see README).
set -euo pipefail
binary=prototypes/memory-scan/out/memory-scan
for transport in sdk bytes; do
  for run in 1 2; do
    echo "##### TRANSPORT=$transport all variants run=$run"
    TRANSPORT=$transport "$binary"
  done
  for only in a b c d; do
    echo "##### TRANSPORT=$transport ONLY=$only"
    TRANSPORT=$transport ONLY=$only "$binary"
  done
done
