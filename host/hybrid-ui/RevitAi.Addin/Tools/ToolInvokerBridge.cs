using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using RevitAi.Abstractions.AI;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Units;
using RevitAi.Core.Units;
using RevitAi.Revit.Units;
using RevitAi.Core.AI;
using RevitAi.Revit;
using Newtonsoft.Json;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

internal sealed class ToolInvokerBridge : IToolInvoker
{
    private readonly RevitAdapter _adapter;
    private readonly AIToolRegistry _registry;
    private readonly DatumPresentationPostProcessor _datumPresentation;
    private readonly object _sync = new();
    // Original ToolLayer stores one pending ExternalEvent request in a static slot.
    private readonly SemaphoreSlim _invocationGate = new(1, 1);
    private IReadOnlyList<ToolDefinition>? _definitions;
    private AIToolInvocationHandler? _handler;
    private bool _initialized;

    public ToolInvokerBridge(RevitAdapter adapter)
    {
        _adapter = adapter;
        _registry = AIToolRegistry.Instance;
        _datumPresentation = new DatumPresentationPostProcessor(adapter);
    }

    public IReadOnlyList<ToolDefinition> Tools
    {
        get
        {
            return EnsureInitialized()
                ? _definitions!
                : Array.Empty<ToolDefinition>();
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
            return new ToolInvocationResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Success = false,
                Payload = new JsonObject
                {
                    ["error"] = $"工具 '{call.Name}' 已从轻量版中屏蔽。"
                },
                ErrorCode = "tool_disabled",
                ErrorMessage = $"工具 '{call.Name}' 已从轻量版中屏蔽。",
                Duration = TimeSpan.Zero
            };
        }

        if (!EnsureInitialized())
        {
            return new ToolInvocationResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Success = false,
                Payload = new JsonObject
                {
                    ["error"] = "Revit 上下文尚未初始化，请先点击 Revit AI > AI Console。"
                },
                ErrorCode = "revit_context_not_ready",
                ErrorMessage = "Revit 上下文尚未初始化，请先点击 Revit AI > AI Console。"
            };
        }

        if (AIRequestManager.HasRequest)
        {
            return new ToolInvocationResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Success = false,
                Payload = new JsonObject
                {
                    ["error"] =
                        "Revit 工具调用通道正忙，已阻止并发请求覆盖。"
                        + "请稍后重试。"
                },
                ErrorCode = "revit_tool_busy",
                ErrorMessage = "Revit 工具调用通道正忙，请稍后重试。"
            };
        }

        var started = DateTimeOffset.UtcNow;

        try
        {
            DatumPresentationPostProcessor.NormalizeExecuteCodeArguments(call);
            var parameters = ConvertArguments(call.Arguments);
            var document = _adapter.GetActiveDocument();
            IProjectUnitService? projectUnitService = null;
            IUnitService? unitService = null;
            if (document is not null)
            {
                projectUnitService = new ProjectUnitService();
                unitService = new UnitService(projectUnitService, document);
            }

            var context = new AIToolContext
            {
                Document = document,
                Parameters = parameters,
                RevitAdapter = _adapter,
                ExternalEvent = _adapter.GetAIToolExternalEvent(),
                SessionId = Guid.NewGuid().ToString("N"),
                ProjectUnitService = projectUnitService,
                UnitService = unitService,
                DataCache = ServiceProvider.GetService<IAIToolDataCache>()
            };

            var gridIdsBefore = call.Name.Equals(
                "execute_code",
                StringComparison.OrdinalIgnoreCase)
                ? DatumPresentationPostProcessor.CollectGridIds(
                    _adapter.GetActiveDocument() as Autodesk.Revit.DB.Document)
                : null;

            var result = await _handler!.HandleToolCallAsync(
                new RevitAi.Core.AI.ToolCallInfo
                {
                    ToolName = call.Name,
                    Parameters = parameters,
                    CallId = call.Id
                },
                context,
                cancellationToken);

            // A generated script that only fails to compile can be repaired and
            // retried: nothing ran yet, so the retry is safe and deterministic.
            string? executeCodeRepair = null;
            if (!result.Success
                && call.Name.Equals(
                    "execute_code",
                    StringComparison.OrdinalIgnoreCase)
                && ExecuteCodeRepair.TryRepair(
                    result.Error,
                    call.Arguments,
                    out var repairNote)
                && call.Arguments["code"] is JsonValue repairedValue
                && repairedValue.TryGetValue<string>(out var repairedCode))
            {
                parameters["code"] = repairedCode;
                var retry = await _handler.HandleToolCallAsync(
                    new RevitAi.Core.AI.ToolCallInfo
                    {
                        ToolName = call.Name,
                        Parameters = parameters,
                        CallId = call.Id
                    },
                    context,
                    cancellationToken);
                if (retry.Success)
                {
                    result = retry;
                    executeCodeRepair = repairNote;
                }
            }

            var payload = CreatePayload(result);
            if (executeCodeRepair is not null)
            {
                payload["executeCodeRepair"] = executeCodeRepair;
            }

            var isExecuteCode = call.Name.Equals(
                "execute_code",
                StringComparison.OrdinalIgnoreCase);
            IReadOnlyCollection<long>? createdGridIds = null;
            if (result.Success && isExecuteCode && gridIdsBefore is not null)
            {
                var after = DatumPresentationPostProcessor
                    .CollectGridIds(
                        _adapter.GetActiveDocument() as Autodesk.Revit.DB.Document);
                createdGridIds = after
                    .Where(id => !gridIdsBefore.Contains(id))
                    .ToList();
            }

            var scriptTouchesGrids = false;
            if (result.Success && isExecuteCode)
            {
                var script = call.Arguments["code"]?.GetValue<string>() ?? string.Empty;
                scriptTouchesGrids = script.Contains("Grid", StringComparison.Ordinal)
                    || script.Contains("grid", StringComparison.Ordinal);
            }

            var shouldApplyPresentation = result.Success
                && (call.Name.Equals(
                        "create_level",
                        StringComparison.OrdinalIgnoreCase)
                    || call.Name.Equals(
                        "create_grid",
                        StringComparison.OrdinalIgnoreCase)
                    || (isExecuteCode
                        && (createdGridIds is { Count: > 0 } || scriptTouchesGrids)));

            if (shouldApplyPresentation)
            {
                try
                {
                    var presentation = await _datumPresentation.ApplyAsync(
                        call.Name,
                        context,
                        payload,
                        createdGridIds,
                        cancellationToken);
                    if (presentation is not null)
                    {
                        payload["datumPresentation"] = presentation;
                    }
                }
                catch (Exception ex)
                {
                    payload["datumPresentationWarning"] =
                        ex.GetBaseException().Message;
                }
            }

            TryLogExecuteCode(call, result);

            return new ToolInvocationResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Success = result.Success,
                Payload = payload,
                ErrorCode = result.Success ? null : "exttools_tool_error",
                ErrorMessage = result.Success ? null : result.Error,
                Duration = DateTimeOffset.UtcNow - started
            };
        }
        catch (Exception ex)
        {
            return new ToolInvocationResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Success = false,
                Payload = new JsonObject
                {
                    ["error"] = ex.GetBaseException().Message
                },
                ErrorCode = "exttools_invocation_error",
                ErrorMessage = ex.GetBaseException().Message,
                Duration = DateTimeOffset.UtcNow - started
            };
        }
    }

    private bool EnsureInitialized()
    {
        lock (_sync)
        {
            if (_initialized)
            {
                return true;
            }

            if (_adapter.GetAIToolExternalEvent() is null)
            {
                return false;
            }

            _registry.SetAdapterAssembly(typeof(RevitAdapter).Assembly);
            _registry.SetRevitAdapter(_adapter);
            _registry.EnsureToolsDiscovered();

            _definitions = _registry.GetAllTools()
                .Select(CreateDefinition)
                .Where(definition => LeanToolPolicy.IsAllowed(definition.Name))
                .OrderBy(x => x.ToolSet, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // The previous UI passes the session tool data cache here; it backs
            // the get_cache_data tool and the cache invalidation on document
            // changes, so it must not be left null.
            _handler = new AIToolInvocationHandler(
                _registry,
                RevitAi.Abstractions.Loader.ServiceProvider
                    .GetService<IAIToolDataCache>());
            _initialized = true;
            return true;
        }
    }

    private static ToolDefinition CreateDefinition(IAITool tool)
    {
        var attribute = tool.GetType().GetCustomAttribute<AIToolAttribute>();
        JsonObject schema;
        try
        {
            schema = JsonNode.Parse(tool.ParametersSchema)?.AsObject() ?? new JsonObject();
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

        return new ToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            ToolSet = attribute?.Category ?? tool.Category,
            Mutating = attribute?.RequiresModification ?? false,
            RequiresTransaction = attribute?.RequiresTransaction ?? false,
            SupportsDryRun = schema.ToJsonString().Contains("\"dry_run\"", StringComparison.Ordinal),
            InputSchema = schema
        };
    }

    private static Dictionary<string, object> ConvertArguments(JsonObject arguments)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in arguments)
        {
            var value = ConvertNode(pair.Value);
            if (value is not null)
            {
                result[pair.Key] = value;
            }
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
            return jsonArray.Select(ConvertNode).Where(x => x is not null).ToList();
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }
            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }
            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue;
            }
            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
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
            try
            {
                payload["data"] = JsonNode.Parse(JsonConvert.SerializeObject(result.Data));
            }
            catch
            {
                payload["data"] = result.Data.ToString();
            }
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
        {
            payload["error"] = result.Error;
        }

        return payload;
    }

    // The AI console only shows the tool result, not the generated script.
    // Keeping the last scripts on disk makes a failing execute_code call
    // diagnosable without another round trip.
    private static void TryLogExecuteCode(ToolCall call, AIToolResult result)
    {
        try
        {
            if (!call.Name.Equals(
                    "execute_code",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var directory = Path.Combine(
                Path.GetTempPath(),
                "RevitAi",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "execute-code.log");
            if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
            {
                File.Delete(path);
            }

            var builder = new StringBuilder();
            builder.Append('[')
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("] success=")
                .Append(result.Success)
                .AppendLine();
            if (!result.Success)
            {
                builder.Append("error: ").AppendLine(result.Error);
            }

            builder.AppendLine("--- code ---");
            builder.AppendLine(
                call.Arguments["code"]?.GetValue<string>() ?? string.Empty);
            builder.AppendLine("--- result ---");
            if (result.Data is not null)
            {
                builder.AppendLine(result.Data.ToString());
            }
            else
            {
                builder.AppendLine(result.Message);
            }

            builder.AppendLine();
            File.AppendAllText(path, builder.ToString());
        }
        catch
        {
        }
    }
}

