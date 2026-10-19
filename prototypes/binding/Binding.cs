// Measurement prototype, not production code: binding MCP tool arguments without creating a string.
// Two builds:
// - Default (ModelContextProtocol 2.2.0 from NuGet), using ONLY what already exists:
//   AIFunctionFactoryOptions.ConfigureParameterBinding (Microsoft.Extensions.AI.Abstractions 10.8.3) +
//   McpServerTool.Create(AIFunction, McpServerToolCreateOptions). The SDK does not let ConfigureParameterBinding through
//   McpServerToolCreateOptions or WithTools<T>, so here the AIFunction is built by hand and what the SDK would do is
//   restored (MarshalResult and the RequestContext/McpServer injection). Nothing disposes the bound values, so a
//   CallToolFilter with try/finally does it (DisposeFilter).
// - SDK_BRANCH (built with -p:McpSdkRoot=<clone at the tool-parameter-binding branch>): the same binders go through
//   McpServerToolCreateOptions.ConfigureParameterBinding and the SDK disposes the bound values, so no filter is needed.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Context the binder receives: the raw value of the other arguments and the request.
public readonly struct BindingContext(AIFunctionArguments arguments, RequestContext<CallToolRequestParams> request)
{
    public RequestContext<CallToolRequestParams> Request => request;

    public bool TryGetArgument(string name, out JsonElement value)
    {
        if (arguments.TryGetValue(name, out object? o) && o is JsonElement e)
        {
            value = e;
            return true;
        }
        value = default;
        return false;
    }

    // Actual type of an argument in AIFunctionArguments (to check that they arrive as JsonElement).
    public string TypeOf(string name) => arguments.TryGetValue(name, out object? o) ? o?.GetType().FullName ?? "null" : "absent";
}

// For own types: static abstract member.
public interface IBindable<TSelf> where TSelf : IBindable<TSelf>
{
    static abstract TSelf Bind(JsonElement value, in BindingContext context);

    static abstract string JsonSchema { get; }
}

// For foreign types (BCL): external binder chosen with an attribute on the parameter.
public interface IBinder<T>
{
    static abstract T Bind(JsonElement value, in BindingContext context);

    static abstract string JsonSchema { get; }
}

public abstract class BindWithAttribute : Attribute
{
    internal abstract object? Bind(JsonElement value, in BindingContext context);

    internal abstract string JsonSchema { get; }
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class BindWithAttribute<TBinder, T> : BindWithAttribute where TBinder : IBinder<T>
{
    internal override object? Bind(JsonElement value, in BindingContext context) => TBinder.Bind(value, context);

    internal override string JsonSchema => TBinder.JsonSchema;
}

public delegate object? BindFunction(JsonElement value, in BindingContext context);

public static class Binding
{
    public const string DisposeKey = "binding-dispose";

    // Table of own bindable types. In AOT a static abstract member cannot be called from a runtime Type
    // (it would need MakeGenericMethod), so each type is registered at compile time.
    private static readonly Dictionary<Type, (BindFunction Bind, string Schema)> s_types = [];

    public static void Register<T>() where T : IBindable<T> =>
        s_types[typeof(T)] = (static (JsonElement v, in BindingContext c) => T.Bind(v, c), T.JsonSchema);

    private static (BindFunction Bind, string Schema)? Find(ICustomAttributeProvider? provider, Type? type)
    {
        if (provider?.GetCustomAttributes(typeof(BindWithAttribute), false) is [BindWithAttribute a, ..])
            return (a.Bind, a.JsonSchema);
        if (type is not null && s_types.TryGetValue(type, out var r))
            return r;
        return null;
    }

    // Schema of an own bindable TYPE (shared by both builds).
    public static AIJsonSchemaCreateOptions SchemaCreateOptions { get; } = new()
    {
                // Schema of an own bindable TYPE. TransformSchemaNode only knows the node's type: for the function's
                // parameters it does not give the ParameterInfo (ParameterAttributeProvider is the one of the constructor
                // associated with a property, AIJsonSchemaCreateContext.cs:66 at ccb356f3; measured: null). Per parameter: SchemaOverridingFunction.
        TransformSchemaNode = static (ctx, node) =>
        {
            if (Environment.GetEnvironmentVariable("DEBUG_SCHEMA") == "1") Console.Error.WriteLine($"TransformSchemaNode path=[{string.Join(",", ctx.Path.ToArray())}] type={ctx.TypeInfo.Type.Name} prov={ctx.ParameterAttributeProvider?.GetType().Name ?? "null"} propprov={ctx.PropertyAttributeProvider?.GetType().Name ?? "null"} attr={ctx.GetCustomAttribute<BindWithAttribute>()?.GetType().Name ?? "null"} node={node.ToJsonString()}");
            if (ctx.Path.Length != 0 || !s_types.TryGetValue(ctx.TypeInfo.Type, out var e))
                return node;
            JsonObject replacement = JsonNode.Parse(e.Schema)!.AsObject();
            if (node is JsonObject old && old["description"] is JsonNode d)
                replacement["description"] = d.DeepClone();
            return replacement;
        },
    };

    // Binding of an own bindable type or of a parameter with [BindWith]; default for anything else.
    // trackForDispose: record the IDisposable results in RequestContext.Items for DisposeFilter (NuGet build only).
    public static AIFunctionFactoryOptions.ParameterBindingOptions CreateBinding(ParameterInfo pi, bool trackForDispose)
    {
        if (Find(pi, pi.ParameterType) is not { } e)
            return default;

        BindFunction bind = e.Bind;
        string argName = pi.Name!;
        bool hasDefault = pi.HasDefaultValue;
        object? defaultValue = pi.DefaultValue;
        return new()
        {
            BindParameter = (p, args) =>
            {
                var request = (RequestContext<CallToolRequestParams>)args.Services!.GetService(typeof(RequestContext<CallToolRequestParams>))!;
                if (!args.TryGetValue(argName, out object? o) || o is null)
                    return hasDefault ? defaultValue : throw new McpException($"Missing argument '{argName}'.");
                if (o is not JsonElement value)
                    throw new McpException($"Argument '{argName}' did not arrive as JsonElement but as {o.GetType().FullName}.");
                object? r = bind(value, new BindingContext(args, request));
                if (trackForDispose && r is IDisposable d)
                {
                    if (!request.Items.TryGetValue(DisposeKey, out object? l) || l is not List<IDisposable> list)
                        request.Items[DisposeKey] = list = [];
                    list.Add(d);
                }
                return r;
            },
        };
    }

#if SDK_BRANCH
    // tool-parameter-binding branch: the SDK keeps its own binding (RequestContext, McpServer, DI, attributes, metadata)
    // and disposes the values the binder returns, so no filter and no Items list.
    public static McpServerTool CreateSdkTool(MethodInfo method, string name, IServiceProvider services, JsonSerializerOptions jsonOptions) =>
        McpServerTool.Create(method, target: null, new McpServerToolCreateOptions
        {
            Name = name,
            Services = services,
            SerializerOptions = jsonOptions,
            SchemaCreateOptions = SchemaCreateOptions,
            ConfigureParameterBinding = static pi => CreateBinding(pi, trackForDispose: false),
        });
#endif

    public static McpServerTool CreateTool(MethodInfo method, string name, IServiceProvider services, JsonSerializerOptions jsonOptions, McpServerToolCreateOptions? toolOptions = null)
    {
        AIFunctionFactoryOptions options = new()
        {
            Name = name,
            SerializerOptions = jsonOptions,
            // Same as the SDK (AIFunctionMcpServerTool.cs:77): without this MEAI serializes the result to JsonElement.
            MarshalResult = static (result, _, _) => new ValueTask<object?>(result),
            JsonSchemaCreateOptions = SchemaCreateOptions,
            ConfigureParameterBinding = pi =>
            {
                // What the SDK injects and is lost when its ConfigureParameterBinding is replaced (only two types here).
                if (pi.ParameterType == typeof(RequestContext<CallToolRequestParams>) || pi.ParameterType == typeof(McpServer))
                    return new() { ExcludeFromSchema = true, BindParameter = static (p, a) => a.Services!.GetService(p.ParameterType) };
                // DI services: copy of AIFunctionMcpServerTool.cs:82-94; only with REPLICATE_DI=1 to measure what happens without it.
                if (Environment.GetEnvironmentVariable("REPLICATE_DI") == "1" &&
                    services.GetService(typeof(Microsoft.Extensions.DependencyInjection.IServiceProviderIsService)) is Microsoft.Extensions.DependencyInjection.IServiceProviderIsService ispis &&
                    ispis.IsService(pi.ParameterType))
                    return new() { ExcludeFromSchema = true, BindParameter = static (p, a) => a.Services?.GetService(p.ParameterType) ?? throw new ArgumentException("No service of the requested type was found.") };

                return CreateBinding(pi, trackForDispose: true);
            },
        };
        AIFunction function = new SchemaOverridingFunction(AIFunctionFactory.Create(method, target: null, options), method);
        toolOptions ??= new McpServerToolCreateOptions();
        toolOptions.Services = services;
        return McpServerTool.Create(function, toolOptions);
    }

    // Guarantees the Dispose: nobody else calls it (neither MEAI nor the SDK). Wraps the whole call, also when a later
    // binder, the tool or the deserialization of another argument throws.
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> DisposeFilter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (ctx, ct) =>
        {
            try
            {
                return await next(ctx, ct).ConfigureAwait(false);
            }
            finally
            {
                if (ctx.Items.TryGetValue(DisposeKey, out object? o) && o is List<IDisposable> list)
                {
                    foreach (IDisposable d in list)
                        d.Dispose();
                    list.Clear();
                }
            }
        };
}

// Schema per PARAMETER (external binder with an attribute, e.g. TimeSpan): properties[name] of the function's already
// generated schema is rewritten by wrapping the AIFunction (DelegatingAIFunction is public in MEAI 10.8.3).
public sealed class SchemaOverridingFunction : DelegatingAIFunction
{
    private readonly JsonElement _schema;

    public SchemaOverridingFunction(AIFunction inner, MethodInfo method) : base(inner)
    {
        JsonObject root = JsonNode.Parse(inner.JsonSchema.GetRawText())!.AsObject();
        foreach (ParameterInfo pi in method.GetParameters())
        {
            if (pi.GetCustomAttributes(typeof(BindWithAttribute), false) is not [BindWithAttribute a, ..])
                continue;
            if (root["properties"]?[pi.Name!] is not JsonObject old)
                continue;
            JsonObject replacement = JsonNode.Parse(a.JsonSchema)!.AsObject();
            if (old["description"] is JsonNode d)
                replacement["description"] = d.DeepClone();
            root["properties"]![pi.Name!] = replacement;
        }
        _schema = JsonSerializer.SerializeToElement(root, TestJsonContext.Default.JsonNode);
    }

    public override JsonElement JsonSchema => _schema;
}
