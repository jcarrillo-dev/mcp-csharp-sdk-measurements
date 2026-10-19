// Bound types of the prototype: InputText (own type, IBindable) and relative durations for TimeSpan (BCL, external binder).
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;

// Class (not struct): BindParameter returns object, so a struct would be boxed and there would be two copies with the
// same array; with a class and a disposed mark, Dispose is idempotent.
[JsonConverter(typeof(UnusedConverter))]
public sealed class InputText : IBindable<InputText>, IDisposable
{
    public static int Created;
    public static int Disposed;

    // For the measurement: also wipe copy E (Arguments) and copy P (params) besides using the own buffer.
    public static bool WipeJsonCopies = Environment.GetEnvironmentVariable("WIPE_JSON") == "1";

    private byte[]? _buffer;

    public int Length { get; }

    public bool IsSecret { get; }

    public int Capacity => _buffer?.Length ?? 0;

    public bool IsPinned { get; }

    public ReadOnlySpan<byte> Bytes => _buffer is null ? throw new ObjectDisposedException(nameof(InputText)) : _buffer.AsSpan(0, Length);

    private InputText(byte[] buffer, int length, bool isSecret, bool isPinned)
    {
        _buffer = buffer;
        Length = length;
        IsSecret = isSecret;
        IsPinned = isPinned;
        Interlocked.Increment(ref Created);
    }

    public static string JsonSchema => """{"type":"string"}""";

    public static InputText Bind(JsonElement value, in BindingContext context)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new McpException("text must be a string.");

        // The other argument first: it decides where the buffer comes from.
        bool secret = context.TryGetArgument("secret", out JsonElement s) && s.ValueKind == JsonValueKind.True;
        Tests.SeenTypes = $"destination:{context.TypeOf("destination")},secret:{context.TypeOf("secret")}";

        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(value); // with quotes and escapes
        byte[] buffer = secret ? GC.AllocateArray<byte>(raw.Length, pinned: true) : ArrayPool<byte>.Shared.Rent(raw.Length);
        Utf8JsonReader reader = new(raw);
        reader.Read();
        int n = reader.CopyString(buffer);

        if (secret && WipeJsonCopies)
        {
            // Copy E (the JsonElement in Arguments) and copy P (params.arguments.text of the JsonRpcRequest). Unsupported
            // usage (it removes the readonly from a byte[] owned by the JsonDocument).
            Zero(raw);
            if (context.Request.JsonRpcRequest.Params?["arguments"]?["text"] is JsonValue v && v.TryGetValue(out JsonElement p))
                Zero(JsonMarshal.GetRawUtf8Value(p));
        }

        return new InputText(buffer, n, secret, secret);
    }

    private static void Zero(ReadOnlySpan<byte> s) => MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(s), s.Length).Clear();

    public void Dispose()
    {
        byte[]? b = Interlocked.Exchange(ref _buffer, null);
        if (b is null)
            return;
        Interlocked.Increment(ref Disposed);
        if (IsSecret)
        {
            // Pinned (POH): no copies from compaction. It is wiped entirely and released; it never goes back to a pool.
            CryptographicOperations.ZeroMemory(b);
            return;
        }
        ArrayPool<byte>.Shared.Return(b);
    }
}

// Only exists so that the JSON source generator gives InputText a JsonTypeInfo (the schema needs it); binding
// never goes through here (BindParameter returns before GetTypeInfo, AIFunctionFactory.cs:925).
public sealed class UnusedConverter : JsonConverter<InputText>
{
    public override InputText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("InputText is only bound through its binder.");

    public override void Write(Utf8JsonWriter writer, InputText value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}

// Relative duration ("90s", "15m", "2h", "3d") read from the UTF-8 bytes, without a string. Own error with McpException:
// the SDK puts its message in the tool's error result (McpServerImpl.cs:1727).
public sealed class RelativeDurationBinder : IBinder<TimeSpan>
{
    public const string Message = "Invalid duration: use a positive integer followed by s, m, h or d (for example 15m).";

    public static string JsonSchema => """{"type":"string","pattern":"^[0-9]+[smhd]$"}""";

    public static TimeSpan Bind(JsonElement value, in BindingContext context) =>
        Parse(value, out TimeSpan r) ? r : throw new McpException(Message);

    internal static bool Parse(JsonElement value, out TimeSpan result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(value);
        Span<byte> text = stackalloc byte[16];
        if (raw.Length > text.Length)
            return false;
        Utf8JsonReader reader = new(raw);
        reader.Read();
        int n = reader.CopyString(text);
        if (n < 2)
            return false;
        ReadOnlySpan<byte> number = text[..(n - 1)];
        if (!Utf8Parser.TryParse(number, out int amount, out int consumed) || consumed != number.Length || amount <= 0)
            return false;
        result = text[n - 1] switch
        {
            (byte)'s' => TimeSpan.FromSeconds(amount),
            (byte)'m' => TimeSpan.FromMinutes(amount),
            (byte)'h' => TimeSpan.FromHours(amount),
            (byte)'d' => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero,
        };
        return result > TimeSpan.Zero;
    }
}

// Same, but throws ArgumentException: to see which message reaches the client when it is not McpException.
public sealed class RelativeDurationBinderArgumentException : IBinder<TimeSpan>
{
    public static string JsonSchema => RelativeDurationBinder.JsonSchema;

    public static TimeSpan Bind(JsonElement value, in BindingContext context) =>
        RelativeDurationBinder.Parse(value, out TimeSpan r) ? r : throw new ArgumentException(RelativeDurationBinder.Message);
}

[JsonSerializable(typeof(InputText))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonNode))]
public sealed partial class TestJsonContext : JsonSerializerContext;
