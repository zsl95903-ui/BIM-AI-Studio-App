using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;
using RevitAi.Core.Tools;
using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Core;
using RevitAi.Engine.Revit;
using RevitAi.Engine.Revit.Implementations;

namespace RevitAi.Addin.Tools;

internal sealed class ToolInvokerBridge : IToolInvoker
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _invocationGate = new(1, 1);
    private UIApplication? _uiApplication;
    private RevitAdapter? _adapter;
    private AIToolRegistry? _registry;
    private RevitToolHost? _host;
    private AIToolInvocationHandler? _handler;
    private SessionAIToolDataCache? _cache;
    private UnitService? _unitService;
    private IReadOnlyList<ToolDefinition>? _definitions;

    public IReadOnlyList<ToolDefinition> Tools =>
        TryEnsureInitialized() && _definitions is not null
            ? _definitions
            : Array.Empty<ToolDefinition>();

    public void Initialize(UIApplication uiApplication)
    {
        ArgumentNullException.ThrowIfNull(uiApplication);
        lock (_sync)
        {
            _uiApplication ??= uiApplication;
        }
    }

    public async Task<ToolInvocationResult> InvokeAsync(
        ToolCall call,
        CancellationToken cancellationToken = default)
    {
        await _invocationGate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeCoreAsync(call, cancellationToken);
        }
        finally
        {
            _invocationGate.Release();
        }
    }

    private async Task<ToolInvocationResult> InvokeCoreAsync(
        ToolCall call,
        CancellationToken cancellationToken)
    {
        if (!LeanToolPolicy.IsAllowed(call.Name))
        {
            return Failure(
                call,
                "tool_disabled",
                $"工具 '{call.Name}' 未在当前版本中启用。",
                TimeSpan.Zero);
        }

        if (!TryEnsureInitialized())
        {
            return Failure(
                call,
                "revit_context_not_ready",
                "Revit 工具上下文尚未初始化，请先打开 AI Console。",
                TimeSpan.Zero);
        }

        var started = DateTimeOffset.UtcNow;
        try
        {
            NormalizeExecuteCodeArguments(call);
            var parameters = ConvertArguments(call.Arguments);
            var document = _uiApplication?.ActiveUIDocument?.Document;
            var context = new AIToolContext
            {
                Document = document,
                Parameters = parameters,
                RevitAdapter = _adapter,
                ExternalEvent = _host?.ExternalEvent,
                SessionId = Guid.NewGuid().ToString("N"),
                DataCache = _cache,
                UnitService = _unitService
            };

            var result = await InvokeToolAsync(call, context, cancellationToken);
            string? repairNote = null;
            if (!result.Success
                && call.Name.Equals("execute_code", StringComparison.OrdinalIgnoreCase)
                && ExecuteCodeRepair.TryRepair(
                    result.Message,
                    call.Arguments,
                    out var note)
                && call.Arguments["code"] is JsonValue codeValue
                && codeValue.TryGetValue<string>(out var repairedCode))
            {
                parameters["code"] = repairedCode;
                var retry = await InvokeToolAsync(call, context, cancellationToken);
                if (retry.Success)
                {
                    result = retry;
                    repairNote = note;
                }
            }

            var payload = CreatePayload(result);
            if (repairNote is not null)
            {
                payload["executeCodeRepair"] = repairNote;
            }

            return new ToolInvocationResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Success = result.Success,
                Payload = payload,
                ErrorCode = result.Success ? null : "tool_error",
                ErrorMessage = result.Success ? null : result.Message,
                Duration = DateTimeOffset.UtcNow - started
            };
        }
        catch (Exception ex)
        {
            return Failure(
                call,
                "tool_invocation_error",
                ex.GetBaseException().Message,
                DateTimeOffset.UtcNow - started);
        }
    }

    private bool TryEnsureInitialized()
    {
        lock (_sync)
        {
            if (_handler is not null)
            {
                return true;
            }

            if (_uiApplication is null)
            {
                return false;
            }

            _adapter = new RevitAdapter(_uiApplication);
            _cache = new SessionAIToolDataCache();
            _unitService = new UnitService();
            _registry = AIToolRegistry.Instance;
            _registry.SetRevitAdapter(_adapter);
            _registry.SetDataCache(_cache);
            _registry.SetUnitService(_unitService);

            RegisterToolImplementations();
            _registry.SetAdapterAssembly(typeof(ExtToolLib.GeneratedToolBase).Assembly);
            _registry.EnsureToolsDiscovered();

            _host = new RevitToolHost();
            _handler = new AIToolInvocationHandler(_registry, _host, _cache);
            _definitions = _registry.GetAllTools()
                .Select(CreateDefinition)
                .Where(definition => LeanToolPolicy.IsAllowed(definition.Name))
                .OrderBy(definition => definition.ToolSet, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return true;
        }
    }

    private Task<AIToolResult> InvokeToolAsync(
        ToolCall call,
        AIToolContext context,
        CancellationToken cancellationToken)
    {
        return _handler!.HandleToolCallAsync(
            new RevitAi.Engine.Abstractions.ToolCallInfo(
                call.Name,
                context.Parameters,
                call.Id),
            context,
            cancellationToken);
    }

    private static ToolDefinition CreateDefinition(IAITool tool)
    {
        var attribute = tool.GetType().GetCustomAttribute<AIToolAttribute>();
        JsonObject schema;
        try
        {
            schema = JsonNode.Parse(tool.ParametersSchema)?.AsObject()
                ?? new JsonObject();
        }
        catch
        {
            schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = true
            };
        }

        var schemaText = schema.ToJsonString();
        return new ToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = schema,
            ToolSet = attribute?.Category ?? tool.Category,
            Mutating = attribute?.RequiresModification ?? false,
            RequiresTransaction = attribute?.RequiresTransaction ?? false,
            SupportsDryRun =
                schemaText.Contains("\"dry_run\"", StringComparison.Ordinal)
                || schemaText.Contains("\"dryRun\"", StringComparison.Ordinal)
        };
    }

    private static Dictionary<string, object?> ConvertArguments(JsonObject arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in arguments)
        {
            result[pair.Key] = ConvertNode(pair.Value);
        }

        return result;
    }

    private static object? ConvertNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject jsonObject)
        {
            return jsonObject.ToDictionary(
                pair => pair.Key,
                pair => ConvertNode(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        if (node is JsonArray jsonArray)
        {
            return jsonArray.Select(ConvertNode).ToList();
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (value.TryGetValue<long>(out var integer))
            {
                return integer;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }
        }

        return node.ToJsonString();
    }

    private static JsonObject CreatePayload(AIToolResult result)
    {
        var payload = new JsonObject();
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            payload["message"] = result.Message;
        }

        if (result.Data is not null)
        {
            payload["data"] = JsonSerializer.SerializeToNode(
                result.Data,
                result.Data.GetType(),
                SerializerOptions);
        }

        if (!string.IsNullOrWhiteSpace(result.CacheId))
        {
            payload["cacheId"] = result.CacheId;
        }

        if (!result.Success)
        {
            payload["error"] = result.Message;
        }

        return payload;
    }

    private static ToolInvocationResult Failure(
        ToolCall call,
        string code,
        string message,
        TimeSpan duration)
    {
        return new ToolInvocationResult
        {
            ToolCallId = call.Id,
            ToolName = call.Name,
            Success = false,
            Payload = new JsonObject { ["error"] = message },
            ErrorCode = code,
            ErrorMessage = message,
            Duration = duration
        };
    }

    private static void NormalizeExecuteCodeArguments(ToolCall call)
    {
        if (!call.Name.Equals("execute_code", StringComparison.OrdinalIgnoreCase)
            || call.Arguments["code"] is not JsonValue codeValue
            || !codeValue.TryGetValue<string>(out var code)
            || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var normalized = code.Replace(
            ".IntegerValue",
            ".Value",
            StringComparison.Ordinal);
        if (!string.Equals(normalized, code, StringComparison.Ordinal))
        {
            call.Arguments["code"] = normalized;
        }
    }

    private static void RegisterToolImplementations()
    {
        ExtToolLibImplementations.RegisterAll();
        ExtToolLib.Implementations.ReadOnlyImplementations.RegisterAll();
        ExtToolLib.Implementations.ReadOnlyImplementations2.RegisterAll();
        ExtToolLib.Implementations.ReadOnlyImplementations3.RegisterAll();
        ExtToolLib.Implementations.WriteImplementations.RegisterAll();
        ExtToolLib.Implementations.WriteImplementations2.RegisterAll();
        ExtToolLib.Implementations.WriteImplementations3.RegisterAll();
        ExtToolLib.Implementations.ParameterImplementations.RegisterAll();
        ExtToolLib.Implementations.MaterialImplementations.RegisterAll();
        ExtToolLib.Implementations.FamilyElementImplementations.RegisterAll();
        ExtToolLib.Implementations.ProjectParameterImplementations.RegisterAll();
        ExtToolLib.Implementations.ModelingImplementations.RegisterAll();
        ExtToolLib.Implementations.ModelingImplementations2.RegisterAll();
        ExtToolLib.Implementations.ElementOpsImplementations.RegisterAll();
        ExtToolLib.Implementations.QueryInteractionImplementations.RegisterAll();
        ExtToolLib.Implementations.DomainImplementations.RegisterAll();
        ExtToolLib.Implementations.DomainImplementations2.RegisterAll();
        ExtToolLib.Implementations.DomainImplementations3.RegisterAll();
        ExtToolLib.Implementations.CodeToolImplementations.RegisterAll();
    }
}
