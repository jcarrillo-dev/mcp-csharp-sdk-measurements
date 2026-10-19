// Measurement prototype for the MCP C# SDK, not production code. A single Native AOT binary, built against
// ModelContextProtocol 2.2.0 from NuGet or, with -p:McpSdkRoot=..., against the tool-parameter-binding branch (SDK_BRANCH):
//   binding server   -> MCP server over stdio (variables: TRANSPORT=bytes, NO_FILTER=1, WITH_WITHTOOLS=1, ECHO=1, WIPE_JSON=1)
//   binding tests    -> client: schemas, escapes, Dispose, TimeSpan errors (starts the server as a child process)
//   binding compare  -> client: the same tool registered in different ways (CMP=withtools|create|aifunction|aifunction_opts)
//   binding scan     -> client: counts copies of the secret in /proc/<child>/mem
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

string mode = args.Length > 0 ? args[0] : "tests";
if (mode == "server")
{
    Binding.Register<InputText>();
    JsonSerializerOptions jsonOptions = new(McpJsonUtilities.DefaultOptions);
    jsonOptions.TypeInfoResolverChain.Insert(0, TestJsonContext.Default);
    jsonOptions.TypeInfoResolverChain.Insert(0, CmpJsonContext.Default);
    jsonOptions.MakeReadOnly();

    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    IMcpServerBuilder mcp = builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "binding", Version = "1" })
        .WithStdioServerTransport();
#if !SDK_BRANCH
    // Only needed with NuGet 2.2.0: on the branch the SDK disposes the bound values.
    if (Environment.GetEnvironmentVariable("NO_FILTER") != "1")
        mcp.WithRequestFilters(f => f.AddCallToolFilter(Binding.DisposeFilter));
#endif
    if (Environment.GetEnvironmentVariable("WITH_WITHTOOLS") == "1")
        mcp.WithTools<SdkTools>(jsonOptions);
    builder.Services.AddSingleton<TestService>();
    mcp.WithTools<Inspection>(jsonOptions);
    switch (Environment.GetEnvironmentVariable("CMP"))
    {
        case "withtools":
            mcp.WithTools<CompareTools>(jsonOptions);
            break;
        case "create":
            builder.Services.AddSingleton(sp => McpServerTool.Create(typeof(CompareTools).GetMethod(nameof(CompareTools.Cmp))!, target: null, new McpServerToolCreateOptions { Services = sp, SerializerOptions = jsonOptions }));
            break;
        case "aifunction_opts":
            builder.Services.AddSingleton(sp =>
            {
                // What DeriveOptions (AIFunctionMcpServerTool.cs:161-213) would do, repeated by hand with the public API.
                var m = typeof(CompareTools).GetMethod(nameof(CompareTools.Cmp))!;
                var a = (McpServerToolAttribute)Attribute.GetCustomAttribute(m, typeof(McpServerToolAttribute))!;
                return Binding.CreateTool(m, "cmp", sp, jsonOptions, new McpServerToolCreateOptions
                {
                    Title = a.Title, ReadOnly = a.ReadOnly, Destructive = a.Destructive, Idempotent = a.Idempotent, OpenWorld = a.OpenWorld,
                    UseStructuredContent = a.UseStructuredContent,
                    Icons = a.IconSource is { } i ? [new() { Source = i }] : null,
                    Metadata = [m, .. m.DeclaringType!.GetCustomAttributes(true), .. m.GetCustomAttributes(true)],
                });
            });
            break;
        case "aifunction":
            builder.Services.AddSingleton(sp => Binding.CreateTool(typeof(CompareTools).GetMethod(nameof(CompareTools.Cmp))!, "cmp", sp, jsonOptions));
            break;
    }

    (string Name, string Method)[] tools =
    [
        ("send", nameof(Tools.Send)),
        ("wait", nameof(Tools.Wait)),
        ("wait_generic", nameof(Tools.WaitGeneric)),
        ("status", nameof(Tools.Status)),
        ("as_string", nameof(Tools.AsString)),
        ("gc", nameof(Tools.Gc)),
    ];
    foreach ((string name, string method) in tools)
#if SDK_BRANCH
        builder.Services.AddSingleton(sp => Binding.CreateSdkTool(typeof(Tools).GetMethod(method)!, name, sp, jsonOptions));
#else
        builder.Services.AddSingleton(sp => Binding.CreateTool(typeof(Tools).GetMethod(method)!, name, sp, jsonOptions));
#endif

    if (Environment.GetEnvironmentVariable("TRANSPORT") == "bytes")
        builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(_ => new BytesTransport()));
    await builder.Build().RunAsync();
    return;
}

if (mode == "tests")
{
    Tests.Run();
    return;
}

if (mode == "compare")
{
    foreach (string v in new[] { "withtools", "create", "aifunction_di", "aifunction_opts" })
    {
        Console.WriteLine("##### CMP=" + v);
        using Client c = v == "aifunction_di" ? new(("CMP", "aifunction"), ("REPLICATE_DI", "1")) : new(("CMP", v), ("REPLICATE_DI", "1"));
        JsonNode list = JsonNode.Parse(c.Call("tools/list", "{}"))!;
        foreach (JsonNode? t in list["result"]!["tools"]!.AsArray())
            if (t!["name"]!.GetValue<string>() == "cmp")
                Console.WriteLine("tools/list: " + t.ToJsonString());
        Console.WriteLine("tools/call: " + c.Tool("cmp", "{\"text\":\"test\"}"));
        Console.WriteLine("Metadata:   " + c.Tool("metadata", "{}"));
    }
    return;
}

if (mode == "scan")
{
    Scan.Run();
    return;
}

public static class Tools
{
    private static readonly bool s_echo = Environment.GetEnvironmentVariable("ECHO") == "1";

    [Description("Sends a text")]
    public static string Send(
        [Description("Destination")] string destination,
        [Description("Text to send")] InputText text,
        [Description("Whether the text is secret")] bool secret = false)
    {
        StringBuilder sb = new($"destination={destination} len={text.Length} secret={text.IsSecret} pinned={text.IsPinned} capacity={text.Capacity}");
        if (s_echo)
            sb.Append(" hex=").Append(Convert.ToHexString(text.Bytes)).Append(" types=").Append(Tests.SeenTypes);
        return sb.ToString();
    }

    public static string Wait([Description("Relative delay")][BindWith<RelativeDurationBinder, TimeSpan>] TimeSpan delay) =>
        "seconds=" + delay.TotalSeconds;

    public static string WaitGeneric([BindWith<RelativeDurationBinderArgumentException, TimeSpan>] TimeSpan delay) =>
        "seconds=" + delay.TotalSeconds;

    public static string Status() => $"created={InputText.Created} disposed={InputText.Disposed}";

    public static string AsString(string text) => "len=" + text.Length;

    public static string Gc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return "ok";
    }
}

// Registered with WithTools<T> (the SDK path): to see what the SDK does with an InputText parameter without a binder.
[McpServerToolType]
public sealed class SdkTools
{
    [McpServerTool(Name = "send_sdk")]
    public static string SendSdk([Description("Text")] InputText text) => "len=" + text.Length;
}

// Minimal stdio client against this same binary in server mode.
public sealed class Client : IDisposable
{
    private readonly Process _child;
    private int _id = 10;

    public int Pid => _child.Id;

    public Client(params (string, string)[] environment)
    {
        ProcessStartInfo psi = new(Environment.ProcessPath!, "server") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach ((string k, string v) in environment)
            psi.Environment[k] = v;
        _child = Process.Start(psi)!;
        _child.StandardInput.AutoFlush = true;
        Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}""");
        _child.StandardOutput.ReadLine();
        Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    }

    public void Send(string line) => _child.StandardInput.Write(line + "\n");

    public string Call(string method, string paramsJson)
    {
        Send("{\"jsonrpc\":\"2.0\",\"id\":" + _id++ + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}");
        return _child.StandardOutput.ReadLine()!;
    }

    public string Tool(string name, string argumentsJson) =>
        Call("tools/call", "{\"name\":\"" + name + "\",\"arguments\":" + argumentsJson + "}");

    public void Dispose()
    {
        _child.Kill();
        _child.Dispose();
    }
}

public static class Tests
{
    public static string SeenTypes = "";

    // Only the text of the response (or the whole JSON-RPC error).
    private static string Text(string response)
    {
        JsonNode n = JsonNode.Parse(response)!;
        if (n["result"] is JsonNode r)
            return (r["isError"]?.GetValue<bool>() == true ? "[isError] " : "") + r["content"]![0]!["text"]!.GetValue<string>();
        return "[JSON-RPC error] " + n["error"]!.ToJsonString();
    }

#if SDK_BRANCH
    private const string Build = "tool-parameter-binding branch: tools created by the SDK, the SDK disposes the bound values, no filter";
    private const string DisposeHeading = "3. Dispose by the SDK, when another argument fails after binding text";
    private const string NoFilterHeading = "5. No filter (NO_FILTER=1 has no effect on this build): does anyone call Dispose?";
#else
    private const string Build = "NuGet 2.2.0: tools created with McpServerTool.Create(AIFunction), bound values disposed by a CallToolFilter";
    private const string DisposeHeading = "3. Dispose with the filter, when another argument fails after binding text";
    private const string NoFilterHeading = "5. Without the filter: does anyone call Dispose?";
#endif

    public static void Run()
    {
        Console.WriteLine("Build: " + Build + "\n");
        Console.WriteLine("=== 1. Schemas (tools/list) with the binder ===");
        using (Client c = new(("ECHO", "1")))
        {
            JsonNode list = JsonNode.Parse(c.Call("tools/list", "{}"))!;
            foreach (JsonNode? t in list["result"]!["tools"]!.AsArray())
                Console.WriteLine($"{t!["name"]}: {t["inputSchema"]!.ToJsonString()}");

            Console.WriteLine("\n=== 2. Escapes: text = p é \\n \\\" \\u0041 z (é as raw UTF-8; \\n, \\\" and \\u0041 escaped) ===");
            const string EscapesText = "\"p\u00e9\\n\\\"\\u0041z\"";
            Console.WriteLine("JSON sent for text: " + EscapesText + "   expected hex 70C3A90A22417A");
            Console.WriteLine("secret=false: " + Text(c.Tool("send", "{\"destination\":\"h\",\"text\":" + EscapesText + ",\"secret\":false}")));
            Console.WriteLine("secret=true:  " + Text(c.Tool("send", "{\"destination\":\"h\",\"text\":" + EscapesText + ",\"secret\":true}")));
            Console.WriteLine("no secret:    " + Text(c.Tool("send", "{\"destination\":\"h\",\"text\":\"abc\"}")));
            Console.WriteLine("status:       " + Text(c.Tool("status", "{}")));

            Console.WriteLine("\n=== " + DisposeHeading + " ===");
            Console.WriteLine("secret=\"x\":   " + Text(c.Tool("send", "{\"destination\":\"h\",\"text\":\"abc\",\"secret\":\"x\"}")));
            Console.WriteLine("status:       " + Text(c.Tool("status", "{}")));
            Console.WriteLine("no text:      " + Text(c.Tool("send", "{\"destination\":\"h\"}")));
            Console.WriteLine("text=5:       " + Text(c.Tool("send", "{\"destination\":\"h\",\"text\":5}")));
            Console.WriteLine("status:       " + Text(c.Tool("status", "{}")));

            Console.WriteLine("\n=== 4. Relative TimeSpan ===");
            foreach (string v in new[] { "\"15m\"", "\"2h\"", "\"90s\"", "\"3d\"", "\"\\u0031\\u0035m\"", "\"15x\"", "\"0m\"", "\"\"", "15", "\"99999999999999999m\"", "\"00:15:00\"" })
                Console.WriteLine($"wait {v,-24} -> " + Text(c.Tool("wait", "{\"delay\":" + v + "}")));
            Console.WriteLine("wait without delay         -> " + Text(c.Tool("wait", "{}")));
            Console.WriteLine("wait_generic \"15x\"         -> " + Text(c.Tool("wait_generic", "{\"delay\":\"15x\"}")));
        }

        Console.WriteLine("\n=== " + NoFilterHeading + " ===");
        using (Client c = new(("NO_FILTER", "1")))
        {
            for (int i = 0; i < 3; i++)
                c.Tool("send", "{\"destination\":\"h\",\"text\":\"abc\",\"secret\":true}");
            c.Tool("gc", "{}");
            Console.WriteLine("3 calls + GC -> " + Text(c.Tool("status", "{}")));
        }

        Console.WriteLine("\n=== 6. SDK path: WithTools<T> with an InputText parameter (no binder) ===");
        using (Client c = new(("WITH_WITHTOOLS", "1")))
        {
            JsonNode list = JsonNode.Parse(c.Call("tools/list", "{}"))!;
            foreach (JsonNode? t in list["result"]!["tools"]!.AsArray())
                if (t!["name"]!.GetValue<string>() == "send_sdk")
                    Console.WriteLine($"send_sdk: {t["inputSchema"]!.ToJsonString()}");
            Console.WriteLine("send_sdk \"abc\" -> " + Text(c.Tool("send_sdk", "{\"text\":\"abc\"}")));
        }
    }
}

public static class Scan
{
    public static void Run()
    {
        int seed = Environment.ProcessId;
        string Secret(char v) => "QZ" + v + "X" + ((long)seed * 7919 % 100000).ToString("D5") + "SECRETOK" + v;
        (string Name, string Value)[] secrets = [("as_string", Secret('A')), ("send", Secret('E'))];
        var environment = new List<(string, string)>();
        foreach (string k in new[] { "TRANSPORT", "WIPE_JSON" })
            if (Environment.GetEnvironmentVariable(k) is { } v)
                environment.Add((k, v));
        Console.WriteLine("Child environment: " + string.Join(" ", environment.Select(e => e.Item1 + "=" + e.Item2)));
        using Client c = new([.. environment]);
        foreach ((string h, string s) in secrets)
        {
            string args = h == "send" ? "{\"destination\":\"h\",\"text\":\"" + s + "\",\"secret\":true}" : "{\"text\":\"" + s + "\"}";
            if (Environment.GetEnvironmentVariable("ONLY") is { } only && only != h)
                continue;
            Console.WriteLine(h + ": " + c.Tool(h, args));
        }
        Console.WriteLine("status: " + c.Tool("status", "{}"));
        Count(c.Pid, secrets, "after the calls");
        c.Tool("gc", "{}");
        Count(c.Pid, secrets, "after a compacting GC in the child");
        for (int i = 0; i < 20; i++)
            c.Tool("send", "{\"destination\":\"h\",\"text\":\"padding-padding-padding-padding-" + i + "\",\"secret\":true}");
        Count(c.Pid, secrets, "after 20 more calls without the secret");
    }

    private static void Count(int pid, (string Name, string Value)[] secrets, string moment)
    {
        var patterns = new List<(string, byte[])>();
        foreach ((string n, string s) in secrets)
        {
            patterns.Add((n + " utf8 ", Encoding.UTF8.GetBytes(s)));
            patterns.Add((n + " utf16", Encoding.Unicode.GetBytes(s)));
        }
        int[] counts = new int[patterns.Count];
        using FileStream mem = new($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, false);
        byte[] buf = new byte[1 << 20];
        const int Overlap = 128;
        foreach (string line in File.ReadAllLines($"/proc/{pid}/maps"))
        {
            string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!p[1].StartsWith('r') || p[1][1] != 'w')
                continue;
            if (p.Length > 5 && (p[5].StartsWith("[vvar") || p[5].StartsWith("[vsyscall")))
                continue;
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
                        if (pos == start || from + k >= Overlap)
                            counts[i]++;
                        from += k + 1;
                    }
                }
                if (read < n)
                    break;
            }
        }
        Console.WriteLine($"-- {moment}");
        for (int i = 0; i < patterns.Count; i++)
            Console.WriteLine($"   {patterns[i].Item1}: {counts[i]}");
    }
}
