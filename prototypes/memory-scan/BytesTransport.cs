// Measurement prototype, not production code: stdio transport that reads BYTES (no StreamReader, no string per line),
// deserializes JsonRpcMessage from ReadOnlySpan<byte> and zeroes what it consumed from its own buffer.
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

public sealed class BytesTransport : TransportBase
{
    private readonly Stream _input = Console.OpenStandardInput();
    private readonly Stream _output = Console.OpenStandardOutput();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reading;

    public BytesTransport() : base("bytes", null)
    {
        SetConnected();
        _reading = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        // Own buffer, pinned (the GC does not move it) and not pooled; it grows by doubling and wiping the previous one.
        byte[] buf = GC.AllocateArray<byte>(64 * 1024, pinned: true);
        int filled = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (filled == buf.Length)
                {
                    byte[] bigger = GC.AllocateArray<byte>(buf.Length * 2, pinned: true);
                    buf.AsSpan(0, filled).CopyTo(bigger);
                    Array.Clear(buf);
                    buf = bigger;
                }
                int n = await _input.ReadAsync(buf.AsMemory(filled), _stop.Token).ConfigureAwait(false);
                if (n == 0) break;
                filled += n;
                int start = 0, nl;
                while ((nl = buf.AsSpan(start, filled - start).IndexOf((byte)'\n')) >= 0)
                {
                    Memory<byte> line = buf.AsMemory(start, nl);
                    if (!line.Span.Trim(" \r\t"u8).IsEmpty)
                    {
                        JsonRpcMessage? m = null;
                        try { m = (JsonRpcMessage?)JsonSerializer.Deserialize(line.Span, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage))); }
                        catch (JsonException) { }
                        if (m is not null) await WriteMessageAsync(m, _stop.Token).ConfigureAwait(false);
                    }
                    line.Span.Clear(); // the line is already deserialized: out of the buffer
                    start += nl + 1;
                }
                buf.AsSpan(start, filled - start).CopyTo(buf);
                buf.AsSpan(filled - start, start).Clear();
                filled -= start;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Array.Clear(buf);
            SetDisconnected();
        }
    }

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)));
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public override async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _input.Dispose();
        try { await _reading.ConfigureAwait(false); } catch { }
        SetDisconnected();
    }
}
