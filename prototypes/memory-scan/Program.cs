// Measurement prototype for the MCP C# SDK 2.2.0, not production code: how many copies of a secret remain in the memory
// of an MCP server that handles secrets (SDK 2.2.0, real stdio transport, Native AOT) after a tools/call, depending on
// how the tool receives the argument.
// The parent process starts this same binary with "server", talks to it over stdin/stdout and scans /proc/<child>/mem
// (ptrace_scope=1 allows the ancestor; no privileges). The parent's copies do not count: only the child is scanned.
// Variants: A = string parameter (the default binding); B = JsonElement parameter + zeroing of its two byte[];
// C = filter that sets the JsonElement aside and leaves "" (the method keeps 'string', schema intact) + zeroing;
// D = incoming message filter that copies the value to an own pinned byte[] before the SDK re-serializes params.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length > 0 && args[0] == "server")
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "memory-scan", Version = "1" })
        .WithStdioServerTransport()
        .WithTools<ScanTools>(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
        .WithRequestFilters(f => f.AddCallToolFilter(ScanTools.SetAsideFilter))
        .WithMessageFilters(f => f.AddIncomingFilter(ScanTools.MessageFilter));
    if (Environment.GetEnvironmentVariable("TRANSPORT") == "bytes")
        builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(_ => new BytesTransport()));
    await builder.Build().RunAsync();
    return;
}

int seed = Environment.ProcessId;
string Secret(char v) => "QZ" + v + "X" + ((long)seed * 7919 % 100000).ToString("D5") + "SECRETOK" + v;
var secrets = new[] { ("a_string", Secret('A')), ("b_element", Secret('B')), ("c_filter", Secret('C')), ("d_message", Secret('D')) };

var psi = new ProcessStartInfo(Environment.ProcessPath!, "server") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
using Process child = Process.Start(psi)!;
StreamWriter w = child.StandardInput;
w.AutoFlush = true;
StreamReader r = child.StandardOutput;
w.Write("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}""" + "\n");
r.ReadLine();
w.Write("""{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n");
int id = 10;
string? only = Environment.GetEnvironmentVariable("ONLY");
foreach ((string tool, string s) in secrets)
{
    if (only is not null && !tool.StartsWith(only)) continue;
    w.Write("{\"jsonrpc\":\"2.0\",\"id\":" + id++ + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{\"text\":\"" + s + "\"}}}\n");
    Console.WriteLine(tool + ": " + r.ReadLine());
}
Scan(child.Id, "after the calls");
w.Write("{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"tools/call\",\"params\":{\"name\":\"gc\",\"arguments\":{}}}\n");
Console.WriteLine("gc: " + r.ReadLine());
Scan(child.Id, "after a compacting GC in the child");
// More traffic without secrets: are the reused buffers overwritten?
for (int i = 0; i < 20; i++)
{
    w.Write("{\"jsonrpc\":\"2.0\",\"id\":" + (200 + i) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"a_string\",\"arguments\":{\"text\":\"padding-padding-padding-padding-" + i + "\"}}}\n");
    r.ReadLine();
}
Scan(child.Id, "after 20 more calls without the secret");
child.Kill();

void Scan(int pid, string moment)
{
    var patterns = new List<(string, byte[])>();
    foreach ((string n, string s) in secrets)
    {
        patterns.Add((n + " utf8 ", Encoding.UTF8.GetBytes(s)));
        patterns.Add((n + " utf16", Encoding.Unicode.GetBytes(s)));
    }
    var counts = new int[patterns.Count];
    using var mem = new FileStream($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, false);
    byte[] buf = new byte[1 << 20];
    const int Overlap = 128;
    foreach (string line in File.ReadAllLines($"/proc/{pid}/maps"))
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!p[1].StartsWith("rw")) continue;
        if (p.Length > 5 && (p[5].StartsWith("[vvar") || p[5].StartsWith("[vsyscall"))) continue;
        string[] rr = p[0].Split('-');
        long start = Convert.ToInt64(rr[0], 16), end = Convert.ToInt64(rr[1], 16);
        for (long pos = start; pos < end; pos += buf.Length - Overlap)
        {
            int n = (int)Math.Min(buf.Length, end - pos);
            int read;
            try { mem.Position = pos; read = mem.Read(buf, 0, n); } catch { break; }
            ReadOnlySpan<byte> v = buf.AsSpan(0, read);
            for (int i = 0; i < patterns.Count; i++)
            {
                ReadOnlySpan<byte> pat = patterns[i].Item2;
                int from = 0, k;
                while ((k = v[from..].IndexOf(pat)) >= 0)
                {
                    if (pos == start || from + k >= Overlap) counts[i]++;
                    from += k + 1;
                }
            }
            if (read < n) break;
        }
    }
    Console.WriteLine($"-- {moment}");
    for (int i = 0; i < patterns.Count; i++)
        Console.WriteLine($"   {patterns[i].Item1}: {counts[i]}");
}

[McpServerToolType]
public sealed class ScanTools
{
    [McpServerTool(Name = "a_string")]
    public static string A([Description("s")] string text) => "len=" + text.Length;

    [McpServerTool(Name = "b_element")]
    public static string B(RequestContext<CallToolRequestParams> ctx, [Description("s")] JsonElement text)
    {
        int n = Use(text);
        Zero(text);
        Zero(ctx.JsonRpcRequest.Params!["arguments"]!["text"]!.GetValue<JsonElement>());
        return "len=" + n;
    }

    [McpServerTool(Name = "c_filter")]
    public static string C(RequestContext<CallToolRequestParams> ctx, [Description("s")] string text)
    {
        var e = (JsonElement)ctx.Items["text-raw"]!;
        int n = Use(e);
        Zero(e);
        Zero(ctx.JsonRpcRequest.Params!["arguments"]!["text"]!.GetValue<JsonElement>());
        return "len=" + n + " string=[" + text + "]";
    }

    [McpServerTool(Name = "d_message")]
    public static string D(RequestContext<CallToolRequestParams> ctx, [Description("s")] string text)
    {
        var (buf, n) = ((byte[], int))ctx.Items["text-secret"]!;
        // ... buf[..n] would be used here ...
        int len = n;
        CryptographicOperations.ZeroMemory(buf);
        return "len=" + len + " string=[" + text + "]";
    }

    // Incoming MESSAGE filter: runs before RequestHandlers re-serializes params (PooledByteBufferWriter).
    // Copies the unescaped value to an own pinned byte[], zeroes the params byte[] and leaves "" in its place.
    public static McpMessageHandler MessageFilter(McpMessageHandler next) =>
        (ctx, ct) =>
        {
            if (ctx.JsonRpcMessage is JsonRpcRequest { Method: "tools/call" } req && req.Params is JsonObject p
                && p["name"]?.GetValue<string>() == "d_message" && p["arguments"] is JsonObject args && args["text"] is JsonValue v
                && v.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.String)
            {
                ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(e);
                byte[] destination = GC.AllocateUninitializedArray<byte>(raw.Length, pinned: true);
                var rd = new Utf8JsonReader(raw);
                rd.Read();
                int n = rd.CopyString(destination);
                MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(raw), raw.Length).Clear();
                args["text"] = "";
                ctx.Items["text-secret"] = (destination, n);
            }
            return next(ctx, ct);
        };

    [McpServerTool(Name = "gc")]
    public static string Gc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return "ok";
    }

    private static readonly JsonElement s_empty = JsonDocument.Parse("\"\"").RootElement;
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> SetAsideFilter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (ctx, ct) =>
        {
            if (ctx.Params?.Name == "c_filter" && ctx.Params.Arguments is { } args && args.TryGetValue("text", out JsonElement original))
            {
                ctx.Items["text-raw"] = original;
                args["text"] = s_empty;
            }
            return next(ctx, ct);
        };

    // Unescapes into a stack buffer, "uses" it and wipes it.
    private static int Use(JsonElement e)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(e);
        Span<byte> destination = stackalloc byte[raw.Length];
        var rd = new Utf8JsonReader(raw);
        rd.Read();
        int n = rd.CopyString(destination);
        destination.Clear();
        return n;
    }

    private static void Zero(JsonElement e)
    {
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(e);
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(raw), raw.Length).Clear();
    }
}
