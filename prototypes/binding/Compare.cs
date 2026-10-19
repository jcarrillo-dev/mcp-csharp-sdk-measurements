// Registration comparison: the same tool registered through WithTools<T>, through McpServerTool.Create(MethodInfo, ...)
// by hand and through McpServerTool.Create(AIFunction, ...) with a custom ConfigureParameterBinding.
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed class TestService
{
    public string Name => "di-service";
}

public sealed record CmpResult(string Text, int N);

[McpServerToolType]
public sealed class CompareTools
{
    [McpServerTool(Name = "cmp", Title = "Comparison", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, IconSource = "https://example.invalid/icon.png")]
    [McpMeta("key", "value")]
    [Description("Comparison tool")]
    public static CmpResult Cmp(
        [Description("Text")] string text,
        RequestContext<CallToolRequestParams> ctx,
        McpServer server,
        TestService service,
        CancellationToken ct) =>
        new($"{text}|ctx={ctx is not null}|srv={server is not null}|svc={service?.Name}", text.Length);
}

[McpServerToolType]
public sealed class Inspection
{
    [McpServerTool(Name = "metadata")]
    public static string Metadata(McpServer server) =>
        server.ServerOptions.ToolCollection!.TryGetPrimitive("cmp", out McpServerTool? t)
            ? string.Join(", ", t.Metadata.Select(m => m.GetType().Name))
            : "no cmp";
}

[JsonSerializable(typeof(CmpResult))]
public sealed partial class CmpJsonContext : JsonSerializerContext;
