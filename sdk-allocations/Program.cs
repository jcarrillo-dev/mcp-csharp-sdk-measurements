// Counts the managed bytes allocated by the whole process per tools/call, with the server reading from a
// StreamServerTransport (the base class of the stdio transport) over in-memory pipes.
// The client side writes pre-encoded requests and scans responses for '\n', so it allocates (almost) nothing itself;
// what changes between SDK builds is the server's read and dispatch path.
// The time per call is wall-clock time on a development machine: treat it as a rough indication only.
//
// Usage: dotnet run -c Release -p:McpSdkRoot=/path/to/csharp-sdk [-- <calls> <argument lengths...>]
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

int calls = args.Length > 0 ? int.Parse(args[0]) : 20_000;
int[] argumentLengths = args.Length > 1 ? args[1..].Select(int.Parse).ToArray() : [16, 1_024, 65_536];
const int Repetitions = 3;

Console.WriteLine($"SDK assembly: {typeof(McpServer).Assembly.GetName().Version} ({Path.GetFileName(typeof(McpServer).Assembly.Location)})");
Console.WriteLine($"Runtime: {Environment.Version}, calls per run: {calls}, runs per size: {Repetitions}");
Console.WriteLine("argument_length\trun\tbytes_per_call\tmicroseconds_per_call");

foreach (int length in argumentLengths)
{
    for (int run = 1; run <= Repetitions; run++)
    {
        (double bytesPerCall, double microsecondsPerCall) = await MeasureAsync(length, calls);
        Console.WriteLine($"{length}\t{run}\t{bytesPerCall:F0}\t{microsecondsPerCall:F1}");
    }
}

static async Task<(double BytesPerCall, double MicrosecondsPerCall)> MeasureAsync(int argumentLength, int calls)
{
    Pipe clientToServer = new();
    Pipe serverToClient = new();
    using CancellationTokenSource cts = new();

    ServiceCollection services = new();
    services.AddMcpServer(options => options.ServerInfo = new() { Name = "allocations", Version = "1.0.0" })
        .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
        .WithTools([McpServerTool.Create((string text) => text.Length, new() { Name = "length" })]);

    await using ServiceProvider provider = services.BuildServiceProvider();
    McpServer server = provider.GetRequiredService<McpServer>();
    Task serverTask = server.RunAsync(cts.Token);

    PipeWriter requests = clientToServer.Writer;
    PipeReader responses = serverToClient.Reader;

    await requests.WriteAsync(Encoding.UTF8.GetBytes(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"allocations","version":"1.0.0"}}}""" + "\n"));
    await ReadLineAsync(responses);
    await requests.WriteAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n"));

    byte[] request = Encoding.UTF8.GetBytes(
        """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"length","arguments":{"text":""" +
        "\"" + new string('a', argumentLength) + "\"}}}\n");

    int warmup = Math.Max(500, calls / 10);
    for (int i = 0; i < warmup; i++)
    {
        await requests.WriteAsync(request);
        await ReadLineAsync(responses);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long before = GC.GetTotalAllocatedBytes(precise: true);
    long started = Stopwatch.GetTimestamp();
    for (int i = 0; i < calls; i++)
    {
        await requests.WriteAsync(request);
        await ReadLineAsync(responses);
    }

    TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
    long after = GC.GetTotalAllocatedBytes(precise: true);

    await cts.CancelAsync();
    await requests.CompleteAsync();
    try { await serverTask; } catch (OperationCanceledException) { }

    return ((after - before) / (double)calls, elapsed.TotalMicroseconds / calls);
}

// Consumes one response line without decoding it.
static async ValueTask ReadLineAsync(PipeReader reader)
{
    while (true)
    {
        ReadResult result = await reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;
        SequencePosition? newLine = buffer.PositionOf((byte)'\n');
        if (newLine is { } position)
        {
            reader.AdvanceTo(buffer.GetPosition(1, position));
            return;
        }

        if (result.IsCompleted)
        {
            throw new EndOfStreamException("The server closed the stream before responding.");
        }

        reader.AdvanceTo(buffer.Start, buffer.End);
    }
}
