# MCP C# SDK measurements: tool argument binding and copies of arguments in memory

Measurements and prototypes behind a set of proposed changes to the [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). The changes are in branches of the fork https://github.com/jcarrillo-dev/csharp-sdk.

## Context

The author maintains an MCP server for SSH, built with the C# SDK and published as a Native AOT binary. Some tool arguments are secrets (passwords, passphrases). As defense in depth, implemented in the server's own code, the server wants to:

- receive those arguments without creating a `string` (a `string` can't be wiped and the GC may leave copies of it behind);
- control how arguments are parsed and validated (custom formats, one argument depending on another, precise error messages);
- wipe the buffers that held a secret when the call ends.

**This is not a vulnerability report.** The SDK behaves correctly. Secrets are just the most demanding example of a server wanting to control how its tool arguments are bound. The questions measured here are:

1. Can a server customize argument binding with what the SDK and `Microsoft.Extensions.AI` offer today, and what does it lose?
2. Where does the SDK copy a tool argument before the tool sees it, and which of those copies are still in memory after the call?
3. How much do two small changes to the SDK's read path change allocations and copies, over stdio and over Streamable HTTP?
4. Who disposes the values a custom binder returns?

Versions: ModelContextProtocol 2.2.0 from NuGet, csharp-sdk `main` at af0fda30 and the branches below on top of it, Microsoft.Extensions.AI.Abstractions 10.8.3, .NET SDK 10.0.201, runtime 10.0.5, Linux x86_64 (`results/environment.txt`).

## Branches and what they cover

| Branch | Commits on `main` | What it changes | Transports |
|---|---|---|---|
| `tools-create-options-callback` | 76dd7ddd | `WithTools`/`WithToolsFromAssembly` overloads with an `Action<MethodInfo, McpServerToolCreateOptions>` callback | any (tool registration) |
| `tool-parameter-binding` | 76dd7ddd, 5d66aa37, d3686e90 | on top of the previous one: `McpServerToolCreateOptions.ConfigureParameterBinding`, and the SDK disposes the values the server's binders return | any (tool invocation) |
| `stdio-pipereader` | fe0500a0 | `StreamServerTransport` reads UTF-8 bytes with `PipeReader` instead of `StreamReader.ReadLineAsync` | stdio and any `Stream` given to `WithStreamServerTransport`; not HTTP |
| `toolcall-from-jsonelement` | c027c8e3, 9e9d32be | request params are deserialized from the `JsonElement` they were read from, not written back from a `JsonNode`; the second commit makes the Streamable HTTP handler keep that path through a new experimental (`MCPEXP002`) accessor, `JsonRpcRequest.GetParamsForReading()` | c027c8e3: stdio and streams only (the HTTP handler reads `Params` through the public property, which falls back to the current path); with 9e9d32be: also Streamable HTTP |

Why an experimental public accessor instead of `InternalsVisibleTo` for HTTP: the repository has no `InternalsVisibleTo` at all (not even for tests), the published 2.2.0 assemblies have none, and when Core needed to expose something to the AspNetCore and Tasks packages (#1722) it used public APIs marked `[Experimental(MCPEXP002)]`. The packages do pin each other to the exact same version (`src/Directory.Build.targets`), so `InternalsVisibleTo` would be safe at run time, but it would go against that pattern. The HTTP commit is separate so it can be dropped if the accessor is not wanted.

## Contents

| Path | What it is |
|---|---|
| `prototypes/binding/` | Native AOT server and client in one binary. Binds tool arguments from the raw `JsonElement` and compares registration paths. Built against NuGet 2.2.0 (`McpServerTool.Create(AIFunction)` plus a `CallToolFilter` that disposes the bound values) or, with `-p:McpSdkRoot=...`, against the `tool-parameter-binding` branch (the SDK creates the tools and disposes the bound values, no filter). Modes: `tests`, `compare`, `scan` (and `server`, used by the others). |
| `prototypes/memory-scan/` | Native AOT server and client in one binary. Four ways for a tool to receive a secret argument (A to D, below) and a scan of the server's memory. Uses NuGet 2.2.0, or a local SDK clone with `-p:McpSdkRoot=...`. |
| `sdk-allocations/` | Allocation counter for `tools/call` over `StreamServerTransport`, built against a local SDK clone. |
| `sdk-allocations-http/` | Allocation counter for `tools/call` over Streamable HTTP (Kestrel on loopback), built against a local SDK clone. |
| `scripts/` | The commands used to produce each file in `results/`. |
| `results/` | Raw outputs, as produced by the commands below. |

Both prototypes publish with `PublishAot`, `TrimMode=full`, `IlLinkTreatWarningsAsErrors`, the trim/AOT analyzers and `TreatWarningsAsErrors`: 0 warnings, 0 errors against NuGet 2.2.0 (`results/publish.txt`) and against every branch used below (a warning would fail the publish).

## Method

### Memory scan

The client process starts the same binary as an MCP server over stdio (`server` mode), sends real JSON-RPC lines, and after the call reads every `rw` region listed in `/proc/<child>/maps` from `/proc/<child>/mem`. It counts how many times the secret appears encoded as UTF-8 and as UTF-16. The parent only scans the child, so the parent's own copies don't count. With `kernel.yama.ptrace_scope` set to 0 or 1 this needs no privileges, because the scanner is the child's parent.

The secret is an 18-character ASCII string built at run time from the process id, so it is never a literal in the binary. Each scan is taken three times: right after the call, after a forced compacting GC in the child, and after 20 more calls without the secret.

Variants in `prototypes/memory-scan` (tool argument `text`):

- **A**: `string text`, the default binding.
- **B**: `JsonElement text`, then the tool zeroes the bytes behind that element and behind the `params` element.
- **C**: a `CallToolFilter` moves the `JsonElement` into `Items` and leaves `""` in the arguments, then zeroes both.
- **D**: an incoming message filter copies the unescaped value into a pinned array, zeroes the `params` bytes and replaces the value with `""` before the SDK deserializes `tools/call`.

Zeroing the bytes behind a `JsonElement` (B, C, D and `WIPE_JSON=1` in the binding prototype) writes into memory owned by `JsonDocument`. It works for this measurement, but it's not a supported use. It's there to tell apart the copies the SDK makes, not as something a server should do.

`TRANSPORT=bytes` replaces the SDK stdio transport with `BytesTransport.cs`, a prototype transport that reads bytes into its own pinned buffer, deserializes from the bytes and zeroes each line after use.

### Allocations over stdio

`sdk-allocations` hosts a server with `WithStreamServerTransport` over in-memory pipes and one tool, `length(string text)`. The client writes pre-encoded `tools/call` lines and scans the responses for `\n`, so it allocates almost nothing itself. After a warm-up it reads `GC.GetTotalAllocatedBytes(precise: true)` before and after 20,000 calls, for arguments of 16, 1,024 and 65,536 characters, three runs each. It counts the whole process.

### Allocations over Streamable HTTP

`sdk-allocations-http` hosts the same tool with `WithHttpTransport()` and `MapMcp()` (default, stateless mode) on Kestrel over loopback TCP, with an `HttpClient` in the same process. It sends pre-encoded bodies, reads each response into a reused buffer and counts the whole process before and after 5,000 calls, three runs per size. Two kinds of request: `2026-07-28` (per-request `_meta` and `Mcp-Method`/`Mcp-Name` headers, which the HTTP handler validates against the body) and `2025-11-25` (only the `MCP-Protocol-Version` header). The client, Kestrel and the response are the same in every build, so only the difference between builds is meaningful; the absolute numbers are mostly HTTP.

In both allocation counters the time per call is wall-clock time on a development machine and varies between runs more than between builds, so no conclusion is drawn from it.

### Disposal of bound values

`binding tests` counts the `InputText` values the binder creates and disposes (`created=N disposed=M`, from the `status` tool), after normal calls, after a call where another argument fails after `text` was bound, and in a server without any filter. It runs once against NuGet 2.2.0 (bound values disposed by a `CallToolFilter` with `try/finally`) and once against the `tool-parameter-binding` branch (disposed by the SDK).

## Reproduce

Requirements: Linux x86_64, .NET SDK 10.0.1xx or later with the ASP.NET Core 10 runtime, the Native AOT prerequisites (clang, zlib), `git`, and `kernel.yama.ptrace_scope` at 0 or 1 for the memory scans. Run every command from the repository root. The outputs in `results/` were produced with `DOTNET_CLI_UI_LANGUAGE=en`.

With NuGet 2.2.0 only:

```bash
# Build both prototypes (Native AOT); 0 warnings expected
dotnet publish prototypes/binding -c Release -r linux-x64 -o prototypes/binding/out
dotnet publish prototypes/memory-scan -c Release -r linux-x64 -o prototypes/memory-scan/out

# Binding prototype
prototypes/binding/out/binding tests > results/binding-tests.txt
prototypes/binding/out/binding compare > results/binding-compare.txt
# The server is expected to abort here (that is the result); with `set -o pipefail` this line returns non-zero
echo | CMP=aifunction prototypes/binding/out/binding server 2>&1 | head -12 > results/binding-compare-no-di.txt
scripts/binding-scan.sh > results/binding-scan.txt

# Ways to receive a secret
scripts/memory-scan.sh > results/memory-scan.txt
```

The measurements of the SDK branches need a clone of the fork that holds them. The scripts check each ref out in a temporary `git worktree`, so the clone isn't modified:

```bash
git clone https://github.com/jcarrillo-dev/csharp-sdk.git ../csharp-sdk
git -C ../csharp-sdk fetch origin stdio-pipereader:stdio-pipereader toolcall-from-jsonelement:toolcall-from-jsonelement tool-parameter-binding:tool-parameter-binding

scripts/sdk-allocations.sh ../csharp-sdk > results/sdk-allocations.txt            # main, stdio-pipereader, toolcall-from-jsonelement
scripts/sdk-allocations-http.sh ../csharp-sdk > results/sdk-allocations-http.txt  # main, toolcall-from-jsonelement~1, toolcall-from-jsonelement
scripts/memory-scan-sdk.sh ../csharp-sdk > results/memory-scan-sdk-branches.txt   # main, stdio-pipereader, toolcall-from-jsonelement
scripts/binding-tests-sdk.sh ../csharp-sdk > results/binding-tests-sdk-branch.txt # tool-parameter-binding
```

`results/publish.txt` was produced with `DOTNET_CLI_UI_LANGUAGE=en dotnet publish ... -clp:Summary`, and `results/environment.txt` from `dotnet --version`, `dotnet --list-runtimes`, `uname -sm` and `/proc/sys/kernel/yama/ptrace_scope`.

## Results

### 1. Binding with what exists today (`results/binding-tests.txt`, `results/binding-compare*.txt`)

`AIFunctionFactoryOptions.ConfigureParameterBinding` already lets a binder read the raw `JsonElement` of every argument (and the request through `AIFunctionArguments.Services`), in Native AOT with 0 warnings. The prototype binds:

- `text` into a pinned buffer when `secret` is `true`, or a pooled one otherwise, unescaping with `Utf8JsonReader.CopyString` and no `string` (escapes checked: a raw UTF-8 `é` and the escapes `\n`, `\"` and a `\u`-escaped `A` arrive as `70C3A90A22417A`);
- `TimeSpan delay` from `"90s"`, `"15m"`, `"2h"` or `"3d"`, with its own error message through `McpException` (any other exception type reaches the client as the generic "An error occurred invoking 'wait'.").

But the SDK only accepts those options through `McpServerTool.Create(AIFunction, ...)`, which replaces the SDK's own tool creation. The same tool, registered four ways:

| Registration | `tools/list` | `tools/call` | `McpServerTool.Metadata` |
|---|---|---|---|
| `WithTools<T>()` | title, annotations, icons, outputSchema, `_meta` | `structuredContent`; `RequestContext`, `McpServer` and DI service injected | method and its attributes |
| `McpServerTool.Create(MethodInfo, ...)` by hand | same | same | same |
| `Create(AIFunction)` with custom binding, DI binding copied by hand | **no** title, annotations, icons or outputSchema | **no** `structuredContent` | **empty** |
| same, plus `McpServerToolCreateOptions` filled by hand from the attributes | same as `WithTools<T>()` | same | same |

Without copying the SDK's DI binding by hand, a tool with a DI-service parameter makes host startup fail with `NotSupportedException: JsonTypeInfo metadata for type 'TestService'`, because the parameter is treated as a JSON argument (`results/binding-compare-no-di.txt`).

### 2. Disposal of bound values (`results/binding-tests.txt`, `results/binding-tests-sdk-branch.txt`)

Nothing in `Microsoft.Extensions.AI` 10.8.3 or in SDK 2.2.0 disposes the values a binder returns.

| Build | Normal calls | + a call where `secret` fails after `text` was bound | Server without the filter, 3 calls + GC |
|---|---|---|---|
| NuGet 2.2.0, `CallToolFilter` with `try/finally` | `created=3 disposed=3` | `created=4 disposed=4` | `created=3 disposed=0` |
| `tool-parameter-binding` branch, no filter (the SDK disposes) | `created=3 disposed=3` | `created=4 disposed=4` | `created=3 disposed=3` |

With the branch the prototype goes through `McpServerTool.Create(MethodInfo, ...)` with `ConfigureParameterBinding`, so it keeps the SDK's own binding and metadata. What it still can't do through the SDK is give one parameter its own JSON schema: on the branch build `wait` shows the default `TimeSpan` schema instead of `"pattern":"^[0-9]+[smhd]$"`, which the NuGet build gets by wrapping the `AIFunction` in a `DelegatingAIFunction`. That's a `Microsoft.Extensions.AI` limit (`ParameterBindingOptions` has no schema), not something the branches change.

The branch's own tests cover the three cases with an `IDisposable` and an `IAsyncDisposable` value: tool succeeds (disposed in reverse order), tool throws, and binding a later parameter fails (only the values already bound exist, and they are disposed). Services bound by the SDK are not disposed.

### 3. Copies left in memory, NuGet 2.2.0 (`results/memory-scan.txt`, `results/binding-scan.txt`)

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
- The binder removes the parameter's own `string` (UTF-16 0 with `BytesTransport`), but on its own it leaves 2 UTF-8 copies: the `params` `JsonElement` (a `byte[]` copy of `params` as the client sent it) and the per-argument `JsonElement` created when `CallToolRequestParams.Arguments` is deserialized. Both exist before any binder runs. They only went to 0 with `WIPE_JSON=1`, which is the unsupported zeroing described above.

### 4. SDK branches over stdio (`results/sdk-allocations.txt`, `results/memory-scan-sdk-branches.txt`)

Allocated bytes per `tools/call`, whole process, second of three runs:

| Argument length | `main` | `stdio-pipereader` | `toolcall-from-jsonelement` |
|---|---|---|---|
| 16 | 6,809 | 6,280 (−7.8%) | 6,649 (−2.3%, −160 B) |
| 1,024 | 13,706 | 11,160 (−18.6%) | 13,544 (−1.2%, −162 B) |
| 65,536 | 465,432 | 333,811 (−28.3%) | 465,310 (−0.03%, −122 B) |

The three runs of each cell differ by at most 66 B (1.1%, the 16-character cell of `stdio-pipereader`) and by less than 0.5% in the other cells.

Memory scan with the SDK's own stdio transport built from each ref, UTF-8 / UTF-16 right after the call (two runs; a range means the two runs differed):

| Variant | `main` | `stdio-pipereader` | `toolcall-from-jsonelement` |
|---|---|---|---|
| A `string` parameter | 2 / 2 | 2 / 1 | 2–3 / 2–3 |
| B `JsonElement` + zeroing | 0 / 1 | 0 / 0 | 0 / 1 |
| C request filter + zeroing | 0 / 1 | 0 / 0 | 0 / 1 |
| D message filter + zeroing | 1 / 2 | 1 / 0 | 1 / 2 |

- `stdio-pipereader` removes the UTF-16 copies that come from the transport: with a `string` parameter only the parameter itself is left (UTF-16 1), and B, C and D have no UTF-16 copy. The UTF-8 copy left in D after the call is in the pipe's buffer; it was gone after 20 more calls.
- `toolcall-from-jsonelement` saves 120–160 B per call and doesn't change what is left at rest. The copy it removes (the write-back of the `JsonNode` into a pooled buffer that is returned without clearing) is transient, and these scans don't see it. A at 3 / 3 in one run is the same run-to-run variation seen with NuGet 2.2.0 (section 3).

### 5. `toolcall-from-jsonelement` over Streamable HTTP (`results/sdk-allocations-http.txt`)

Allocated bytes per `tools/call`, whole process (server, Kestrel and client), second of three runs:

| Request | Argument length | `main` | c027c8e3 (stdio only) | 9e9d32be (branch head, HTTP covered) |
|---|---|---|---|---|
| `2025-11-25` | 16 | 34,883 | 34,906 (+23 B) | 34,729 (−154 B, −0.44%) |
| `2025-11-25` | 1,024 | 39,952 | 39,969 (+17 B) | 39,800 (−152 B, −0.38%) |
| `2025-11-25` | 65,536 | 365,032 | 365,014 (−18 B) | 364,905 (−127 B, −0.03%) |
| `2026-07-28` | 16 | 38,829 | 38,869 (+40 B) | 38,687 (−142 B, −0.37%) |
| `2026-07-28` | 1,024 | 43,821 | 43,728 (−93 B) | 43,576 (−245 B, −0.56%) |
| `2026-07-28` | 65,536 | 368,553 | 368,548 (−5 B) | 368,348 (−205 B, −0.06%) |

- With only the first commit, HTTP gets no reduction: the differences are within run-to-run variation, because the HTTP handler reads `Params` and the request falls back to the current path. With the second commit HTTP saves about the same as stdio (127–154 B per call with `2025-11-25`).
- Run-to-run variation: at most 21 B per cell with `2025-11-25`, so the saving there is well above it. With `2026-07-28` it reaches 274 B (0.63%) in the 1,024-character cells, about the size of the saving there; the direction is the same in every cell, but those cells alone would not show it.
- The branch's tests check the behaviour directly: over HTTP, a tool receives the argument exactly as the client escaped it (an argument sent as `"p\u00e9\u0041"` reaches the tool unchanged instead of being rewritten as `"p\u00E9A"`), and message filters that read or modify `Params` keep working.

## What the branches remove, and what they don't

They remove:

- the UTF-16 copies of the argument that come from the stdio transport (`stdio-pipereader`): the line `string` and the reader's `char` buffer;
- the parameter's own UTF-16 `string`, when the server binds the argument itself (`tools-create-options-callback` + `tool-parameter-binding`; measured with the NuGet prototype, which uses the same `Microsoft.Extensions.AI` binding);
- the write-back of params into a pooled buffer that is returned without clearing, and the re-escaping of arguments (`toolcall-from-jsonelement`, over stdio and, with its second commit, over HTTP);
- allocations: −7.8% to −28.3% per call over stdio with `stdio-pipereader`, 120–160 B per call with `toolcall-from-jsonelement` (over stdio, and over HTTP with its second commit);
- the need for a `CallToolFilter` with `try/finally` to dispose bound values (`tool-parameter-binding`).

They don't remove the **two UTF-8 copies the SDK makes before any binder runs**: the `params` `JsonElement` and the per-argument `JsonElement` in `CallToolRequestParams.Arguments`. In these measurements they only went to 0 with `WIPE_JSON=1`, which overwrites memory owned by `JsonDocument` and is not supported. None of the branches changes that.

## Possible follow-up (not a branch)

What would address those two UTF-8 copies is letting the transport or reader own the buffer the message is parsed from: rent it, keep the `JsonElement`s pointing into it while the message is handled, and clear and return it when the message is released (for example behind a server option, since it changes how long a `JsonElement` from a message stays valid). It's left as a point for discussion, not implemented or measured here, so there is no claim about how many copies it would remove.

## Limits

- **Two UTF-8 copies remain** with every branch (above). The zeroing used to tell them apart (`WIPE_JSON=1`, and variants B, C and D) writes into `JsonDocument` memory and is not a supported use.
- **Variation between runs.** Copy counts vary slightly between runs (for example A with the SDK transport: 2 or 3), because what survives depends on the GC and buffer reuse. Allocation counts vary by up to 1.1% per cell over stdio and up to 0.63% over HTTP. The raw files keep every run. Time per call is not used.
- **What was not measured:** memory scans over HTTP (only allocations); memory scans of the binding prototype built against `tool-parameter-binding` (only `created`/`disposed`); non-ASCII secrets, which would appear in other forms (for example re-escaped) that these patterns don't match; copies that exist only during the call; the client process, kernel pipe buffers, and anything after the tool (for example an SSH library); `StreamClientSessionTransport`, the legacy SSE handler and the Tasks extension, which read `Params` through the public property.
- **Snapshots at rest.** "0" means none were found at those three moments, not that none ever existed.
- **Only `rw` regions** of the server process.
- **Allocations are whole-process** and include the server's response serialization (and, over HTTP, Kestrel and the client), which are the same in every build.
- **One machine, one runtime** (.NET 10.0.5, Linux x86_64).

## License

Apache License 2.0, see `LICENSE`.
