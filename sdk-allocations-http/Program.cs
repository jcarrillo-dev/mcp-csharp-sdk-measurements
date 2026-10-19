// Counts the managed bytes allocated by the whole process per tools/call over Streamable HTTP: an ASP.NET Core server
// with MapMcp (default, stateless mode) on Kestrel over loopback TCP, and an HttpClient in the same process.
// The client sends pre-encoded request bodies and reads each response into a reused buffer; its own allocations
// (HttpClient, headers, Kestrel) are the same in every SDK build, so what changes between builds is the server's
// read, validation and dispatch path. Absolute numbers are much higher than over stdio because of HTTP itself.
// Two kinds of request:
//   2026-07-28  per-request _meta and Mcp-Method/Mcp-Name headers (the HTTP handler validates both against the body)
//   2025-11-25  MCP-Protocol-Version header only, no _meta
// The time per call is wall-clock time on a development machine: treat it as a rough indication only.
//
// Usage: dotnet run -c Release -p:McpSdkRoot=/path/to/csharp-sdk [-- <calls> <argument lengths...>]
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

int calls = args.Length > 0 ? int.Parse(args[0]) : 5_000;
int[] argumentLengths = args.Length > 1 ? args[1..].Select(int.Parse).ToArray() : [16, 1_024, 65_536];
string[] protocols = ["2026-07-28", "2025-11-25"];
const int Repetitions = 3;

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "allocations", Version = "1.0.0" })
    .WithHttpTransport()
    .WithTools([McpServerTool.Create((string text) => text.Length, new() { Name = "length" })]);

await using WebApplication app = builder.Build();
app.MapMcp();
await app.StartAsync();
string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

using HttpClient client = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
client.DefaultRequestHeaders.Accept.Add(new("application/json"));
client.DefaultRequestHeaders.Accept.Add(new("text/event-stream"));
byte[] responseBuffer = new byte[1 << 20];

Console.WriteLine($"SDK assemblies: {typeof(McpServer).Assembly.GetName().Version} ({Path.GetFileName(typeof(McpServer).Assembly.Location)}), " +
    $"{typeof(McpEndpointRouteBuilderExtensions).Assembly.GetName().Name}");
Console.WriteLine($"Runtime: {Environment.Version}, calls per run: {calls}, runs per size: {Repetitions}");
Console.WriteLine("protocol\targument_length\trun\tbytes_per_call\tmicroseconds_per_call");

foreach (string protocol in protocols)
{
    foreach (int length in argumentLengths)
    {
        for (int run = 1; run <= Repetitions; run++)
        {
            (double bytesPerCall, double microsecondsPerCall) = await MeasureAsync(protocol, length, calls);
            Console.WriteLine($"{protocol}\t{length}\t{run}\t{bytesPerCall:F0}\t{microsecondsPerCall:F1}");
        }
    }
}

await app.StopAsync();

async Task<(double BytesPerCall, double MicrosecondsPerCall)> MeasureAsync(string protocol, int argumentLength, int calls)
{
    string meta = protocol == "2026-07-28"
        ? ""","_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"allocations","version":"1.0.0"},"io.modelcontextprotocol/clientCapabilities":{}}"""
        : "";
    byte[] body = Encoding.UTF8.GetBytes(
        """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"length","arguments":{"text":""" +
        "\"" + new string('a', argumentLength) + "\"}" + meta + "}}");

    // Checks once that the call succeeds, so a rejected request is not measured as a cheap one.
    string first = await CallAsync(protocol, body, decode: true);
    if (!first.Contains($"\"text\":\"{argumentLength}\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Unexpected response: " + first);
    }

    int warmup = Math.Max(200, calls / 10);
    for (int i = 0; i < warmup; i++)
    {
        await CallAsync(protocol, body, decode: false);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long before = GC.GetTotalAllocatedBytes(precise: true);
    long started = Stopwatch.GetTimestamp();
    for (int i = 0; i < calls; i++)
    {
        await CallAsync(protocol, body, decode: false);
    }

    TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
    long after = GC.GetTotalAllocatedBytes(precise: true);
    return ((after - before) / (double)calls, elapsed.TotalMicroseconds / calls);
}

async Task<string> CallAsync(string protocol, byte[] body, bool decode)
{
    using HttpRequestMessage request = new(HttpMethod.Post, "") { Content = new ByteArrayContent(body) };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    request.Headers.Add("MCP-Protocol-Version", protocol);
    if (protocol == "2026-07-28")
    {
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", "length");
    }

    using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (response.StatusCode != HttpStatusCode.OK)
    {
        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    await using Stream stream = await response.Content.ReadAsStreamAsync();
    int total = 0, read;
    while ((read = await stream.ReadAsync(responseBuffer.AsMemory(total))) > 0)
    {
        total += read;
    }

    return decode ? Encoding.UTF8.GetString(responseBuffer, 0, total) : "";
}
