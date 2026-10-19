# MCP C# SDK measurements: tool argument binding and copies of arguments in memory

Measurements and prototypes behind a set of proposed changes to the [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Context

The author maintains an MCP server for SSH, built with the C# SDK and published as a Native AOT binary. Some tool arguments are secrets (passwords, passphrases). As defense in depth, implemented in the server's own code, the server wants to:

- receive those arguments without creating a `string` (a `string` can't be wiped and the GC may leave copies of it behind);
- control how arguments are parsed and validated (custom formats, one argument depending on another, precise error messages);
- wipe the buffers that held a secret when the call ends.

**This is not a vulnerability report.** The SDK behaves correctly. Secrets are just the most demanding example of a server wanting to control how its tool arguments are bound. The questions measured here are:

1. Can a server customize argument binding with what the SDK and `Microsoft.Extensions.AI` offer today, and what does it lose?
2. Where does the SDK copy a tool argument before the tool sees it, and which of those copies are still in memory after the call?
3. How much do two small changes to the SDK's read path change allocations and copies?

Versions: ModelContextProtocol 2.2.0 from NuGet, csharp-sdk `main` at af0fda30 and branches on top of it, Microsoft.Extensions.AI.Abstractions 10.8.3, .NET SDK 10.0.201, runtime 10.0.5, Linux x86_64 (`results/environment.txt`).

## Contents

| Path | What it is |
|---|---|
| `prototypes/binding/` | Native AOT server and client in one binary. Binds tool arguments from the raw `JsonElement` with `AIFunctionFactoryOptions.ConfigureParameterBinding`, registers tools with `McpServerTool.Create(AIFunction)`, and compares registration paths. Modes: `tests`, `compare`, `scan` (and `server`, used by the others). |
| `prototypes/memory-scan/` | Native AOT server and client in one binary. Four ways for a tool to receive a secret argument (A to D, below) and a scan of the server's memory. Uses NuGet 2.2.0, or a local SDK clone with `-p:McpSdkRoot=...`. |
| `sdk-allocations/` | Allocation counter for `tools/call` over `StreamServerTransport`, built against a local SDK clone. |
| `scripts/` | The commands used to produce each file in `results/`. |
| `results/` | Raw outputs, as produced by the commands below. |

Both prototypes publish with `PublishAot`, `TrimMode=full`, `IlLinkTreatWarningsAsErrors`, the trim/AOT/single-file analyzers and `TreatWarningsAsErrors`: 0 warnings, 0 errors (`results/publish.txt`).

## Method

### Memory scan

The client process starts the same binary as an MCP server over stdio (`server` mode), sends real JSON-RPC lines, and after the call reads every `rw` region listed in `/proc/<child>/maps` from `/proc/<child>/mem`. It counts how many times the secret appears encoded as UTF-8 and as UTF-16. The parent only scans the child, so the parent's own copies don't count. With `kernel.yama.ptrace_scope` set to 0 or 1 this needs no privileges, because the scanner is the child's parent.

The secret is an 18-character ASCII string built at run time from the process id, so it is never a literal in the binary. Each scan is taken three times: right after the call, after a forced compacting GC in the child, and after 20 more calls without the secret.

Variants in `prototypes/memory-scan` (tool argument `text`):

- **A**: `string text`, the default binding.
- **B**: `JsonElement text`, then the tool zeroes the bytes behind that element and behind the `params` element.
- **C**: a `CallToolFilter` moves the `JsonElement` into `Items` and leaves `""` in the arguments, then zeroes both.
- **D**: an incoming message filter copies the unescaped value into a pinned array, zeroes the `params` bytes and replaces the value with `""` before the SDK deserializes `tools/call`.

Zeroing the bytes behind a `JsonElement` (B, C, D and `WIPE_JSON=1`) writes into memory owned by `JsonDocument`. It works for this measurement, but it's not a supported use. It's there to tell apart the copies the SDK makes.

`TRANSPORT=bytes` replaces the SDK stdio transport with `BytesTransport.cs`, a prototype transport that reads bytes into its own pinned buffer, deserializes from the bytes and zeroes each line after use.

### Allocations

`sdk-allocations` hosts a server with `WithStreamServerTransport` over in-memory pipes and one tool, `length(string text)`. The client writes pre-encoded `tools/call` lines and scans the responses for `\n`, so it allocates almost nothing itself. After a warm-up it reads `GC.GetTotalAllocatedBytes(precise: true)` before and after 20,000 calls, for arguments of 16, 1,024 and 65,536 characters, three runs each. It counts the whole process. The time per call is wall-clock time on a development machine and only a rough indication.

## Reproduce

Requirements: Linux x86_64, .NET SDK 10.0.1xx or later, the Native AOT prerequisites (clang, zlib), `git`, and `kernel.yama.ptrace_scope` at 0 or 1 for the memory scans. Run every command from the repository root.

```bash
# Build both prototypes (Native AOT); 0 warnings expected
dotnet publish prototypes/binding -c Release -r linux-x64 -o prototypes/binding/out
dotnet publish prototypes/memory-scan -c Release -r linux-x64 -o prototypes/memory-scan/out

# Binding prototype
prototypes/binding/out/binding tests > results/binding-tests.txt
prototypes/binding/out/binding compare > results/binding-compare.txt
echo | CMP=aifunction prototypes/binding/out/binding server 2>&1 | head -12 > results/binding-compare-no-di.txt
scripts/binding-scan.sh > results/binding-scan.txt

# Ways to receive a secret, NuGet 2.2.0
scripts/memory-scan.sh > results/memory-scan.txt
```

The two measurements of the SDK branches need a clone of the fork that holds the branches (https://github.com/jcarrillo-dev/csharp-sdk). The scripts check each ref out in a temporary `git worktree`, so the clone isn't modified:

```bash
git clone https://github.com/jcarrillo-dev/csharp-sdk.git ../csharp-sdk
git -C ../csharp-sdk fetch origin stdio-pipereader:stdio-pipereader toolcall-from-jsonelement:toolcall-from-jsonelement
scripts/sdk-allocations.sh ../csharp-sdk > results/sdk-allocations.txt
scripts/memory-scan-sdk.sh ../csharp-sdk > results/memory-scan-sdk-branches.txt
```

`results/publish.txt` was produced with `DOTNET_CLI_UI_LANGUAGE=en dotnet publish ... -clp:Summary`, and `results/environment.txt` from `dotnet --version`, `dotnet --list-runtimes`, `uname -sm` and `/proc/sys/kernel/yama/ptrace_scope`.

## Results

### 1. Binding with what exists today (`results/binding-tests.txt`, `results/binding-compare*.txt`)

`AIFunctionFactoryOptions.ConfigureParameterBinding` already lets a binder read the raw `JsonElement` of every argument (and the request through `AIFunctionArguments.Services`), in Native AOT with 0 warnings. The prototype binds:

- `text` into a pinned buffer when `secret` is `true`, or a pooled one otherwise, unescaping with `Utf8JsonReader.CopyString` and no `string` (escapes checked: `"pé\n\"Az"` arrives as `70C3A90A22417A`);
- `TimeSpan delay` from `"90s"`, `"15m"`, `"2h"` or `"3d"`, with its own error message through `McpException` (any other exception type reaches the client as the generic "An error occurred invoking 'wait'.");
- disposal in a `CallToolFilter` with `try/finally`: `created=4 disposed=4`, including a call where another argument fails after `text` was bound. Without the filter, nothing disposes the bound values: `created=3 disposed=0`.

But the SDK only accepts those options through `McpServerTool.Create(AIFunction, ...)`, which replaces the SDK's own tool creation. The same tool, registered four ways:

| Registration | `tools/list` | `tools/call` | `McpServerTool.Metadata` |
|---|---|---|---|
| `WithTools<T>()` | title, annotations, icons, outputSchema, `_meta` | `structuredContent`; `RequestContext`, `McpServer` and DI service injected | method and its attributes |
| `McpServerTool.Create(MethodInfo, ...)` by hand | same | same | same |
| `Create(AIFunction)` with custom binding, DI binding copied by hand | **no** title, annotations, icons or outputSchema | **no** `structuredContent` | **empty** |
| same, plus `McpServerToolCreateOptions` filled by hand from the attributes | same as `WithTools<T>()` | same | same |

Without copying the SDK's DI binding by hand, a tool with a DI-service parameter makes host startup fail with `NotSupportedException: JsonTypeInfo metadata for type 'TestService'`, because the parameter is treated as a JSON argument (`results/binding-compare-no-di.txt`).

### 2. Copies left in memory, NuGet 2.2.0 (`results/memory-scan.txt`, `results/binding-scan.txt`)

UTF-8 / UTF-16 occurrences of the secret right after the call, two runs:

| Variant | SDK stdio transport | `BytesTransport` |
|---|---|---|
| A `string` parameter | 3 / 3 | 2 / 1 |
| B `JsonElement` + zeroing | 0 / 1 | 0 / 0 |
| C request filter + zeroing | 0 / 1 | 0 / 0 |
| D message filter + zeroing | 1 / 2 | 0 / 0 |

Binding prototype (`send`, `secret: true`, binder into a pinned buffer that is wiped on dispose):

| Variant | After the call | After 20 more calls |
|---|---|---|
| Binder only, SDK transport | 3 / 2 | 2 / 1 |
| Binder + zeroing the JSON (`WIPE_JSON=1`), SDK transport | 1 / 2 | 0 / 1 |
| Binder only, `BytesTransport` | 2 / 0 | 2 / 0 |
| Binder + zeroing the JSON, `BytesTransport` | 0 / 0 | 0 / 0 |

Reading of the tables (inferred from the SDK and runtime source, not measured one by one):

- The UTF-16 copies that remain with the SDK transport and disappear with `BytesTransport` come from the transport: the `string` returned by `StreamReader.ReadLineAsync` and the reader's `char` buffer.
- The binder removes the parameter's own `string` (UTF-16 0), but on its own it leaves 2 UTF-8 copies: the `params` `JsonElement` (a `byte[]` copy of `params` as the client sent it) and the per-argument `JsonElement` created when `CallToolRequestParams.Arguments` is deserialized. Both exist before any binder runs.

### 3. SDK branches

`stdio-pipereader` changes `StreamServerTransport` to read with `PipeReader` and deserialize from UTF-8 bytes. `toolcall-from-jsonelement` deserializes request params from the `JsonElement` they were read from instead of writing the `JsonNode` back to a pooled buffer and parsing it again.

Allocated bytes per `tools/call`, whole process, second of three runs (`results/sdk-allocations.txt`):

| Argument length | `main` | `stdio-pipereader` | `toolcall-from-jsonelement` |
|---|---|---|---|
| 16 | 6,808 | 6,282 (−7.7%) | 6,648 (−2.4%) |
| 1,024 | 13,704 | 11,160 (−18.6%) | 13,544 (−1.2%) |
| 65,536 | 465,470 | 333,811 (−28.3%) | 465,311 (−0.03%) |

The three runs of each cell differ by at most 1.5% (the 16-character cell of `stdio-pipereader`), and by less than 1% in the other cells. Time per call is in the raw file; it varies between runs more than between builds, so no conclusion is drawn from it.

Memory scan with the SDK's own stdio transport built from each ref, UTF-8 / UTF-16 right after the call (`results/memory-scan-sdk-branches.txt`, two runs):

| Variant | `main` | `stdio-pipereader` | `toolcall-from-jsonelement` |
|---|---|---|---|
| A `string` parameter | 2 / 2 | 2–3 / 1 | 2 / 2 |
| B `JsonElement` + zeroing | 0 / 1 | 0 / 0 | 0 / 1 |
| C request filter + zeroing | 0 / 1 | 0 / 0 | 0 / 1 |
| D message filter + zeroing | 1 / 2 | 1 / 0 | 1 / 2 |

- `stdio-pipereader` removes the UTF-16 copies that come from the transport. The UTF-8 copy left in D after the call is in the pipe's buffer; it was gone after 20 more calls.
- `toolcall-from-jsonelement` saves about 160 B per call and doesn't change what is left at rest. The copy it removes (the write-back into a pooled buffer that is returned without clearing) is transient, and these scans don't see it. Its effect is that fewer copies are made and arguments reach the tool as the client sent them (no re-escaping).

## Limits

- **Only stdio and in-memory streams.** Streamable HTTP isn't measured. In `toolcall-from-jsonelement` the HTTP handler reads `Params` through the public property, so HTTP keeps the current path.
- **ASCII secrets only.** A secret with non-ASCII characters would appear in other forms (for example `é` after re-escaping) that these patterns don't match.
- **Snapshots at rest.** Copies that exist only during the call are not seen. "0" means none were found at those three moments, not that none ever existed.
- **Only `rw` regions** of the process, and only the server process. Kernel pipe buffers, the client and anything after the tool (for example an SSH library) are out of scope.
- **Zeroing `JsonElement` memory is unsupported** and is only a measurement device.
- **Counts vary slightly between runs** (for example A with the SDK transport: 2 or 3), because what survives depends on GC and buffer reuse. The raw files keep every run.
- **Allocations are whole-process** and include the server's response serialization, which is the same in every build.
- **One machine, one runtime** (.NET 10.0.5, Linux x86_64).

## License

Apache License 2.0, see `LICENSE`.
