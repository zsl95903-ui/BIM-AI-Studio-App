using System.Text.Json.Nodes;
using System.IO;
using RevitAi.Core.Agent;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Services;

internal sealed class AgentRuntimeService : IDisposable
{
    private readonly IToolInvoker _tools;
    private readonly AttachmentPreprocessor _attachmentPreprocessor = new();
    private readonly RuntimeSettingsStore _settingsStore = new();
    private readonly AgentSessionStore _sessionStore = new();
    private AgentSessionState _session = new();
    private readonly RuntimeSettings _settings;
    private AgentLoop? _agentLoop;

    public AgentRuntimeService(IToolInvoker tools)
    {
        _tools = tools;
        _settings = _settingsStore.Load();
    }

    public string ApiUrl => _settings.ApiUrl;

    public string Model => _settings.Model;

    public bool HasApiKey => _settings.HasApiKey;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiUrl)
        && !string.IsNullOrWhiteSpace(Model)
        && HasApiKey;

    public async Task<AgentTurnResult> RunAsync(
        string prompt,
        CancellationToken cancellationToken = default,
        Func<AgentProgressEvent, Task>? progress = null,
        IReadOnlyList<AgentAttachment>? attachments = null)
    {
        _agentLoop ??= CreateAgentLoop();
        var preparedAttachments = await _attachmentPreprocessor.PrepareAsync(
            attachments ?? Array.Empty<AgentAttachment>(),
            cancellationToken);

        // The ported original loop persists the whole turn (user message, every
        // tool exchange and the final answer) into the session by itself.
        var result = await _agentLoop.RunAsync(
            _session,
            prompt,
            cancellationToken,
            progress,
            preparedAttachments);

        // Persist the session after every completed turn.
        _sessionStore.Save(_session);
        return result;
    }

    public void Reset()
    {
        _session.Clear();
    }

    public JsonArray ListSessions()
    {
        var array = new JsonArray();
        foreach (var item in _sessionStore.List())
        {
            array.Add(new JsonObject
            {
                ["sessionId"] = item.SessionId,
                ["title"] = item.Title,
                ["updatedAt"] = item.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
                ["messageCount"] = item.MessageCount,
                ["roundCount"] = item.RoundCount,
                ["hasSummary"] = item.HasSummary,
                ["active"] = item.SessionId == _session.SessionId
            });
        }

        return array;
    }

    public JsonObject SaveCurrentSession()
    {
        _sessionStore.Save(_session);
        return GetStatus();
    }

    public JsonObject NewSession()
    {
        _sessionStore.Save(_session);
        _session = new AgentSessionState();
        return GetStatus();
    }

    public JsonObject OpenSession(string sessionId)
    {
        var loaded = _sessionStore.Load(sessionId);
        if (loaded is not null)
        {
            _sessionStore.Save(_session);
            _session = loaded;
        }

        return GetStatus();
    }

    public JsonObject DeleteSession(string sessionId)
    {
        _sessionStore.Delete(sessionId);
        if (string.Equals(sessionId, _session.SessionId, StringComparison.Ordinal))
        {
            _session = new AgentSessionState();
        }

        return GetStatus();
    }

    public (bool Success, string Message) ExportCurrentSession()
    {
        if (_session.Messages.Count == 0)
        {
            return (false, "当前会话还没有内容。");
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitAi");
            System.IO.Directory.CreateDirectory(directory);
            var safeTitle = string.Join(
                "_",
                _session.Title.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(
                directory,
                $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.md");

            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"# {_session.Title}");
            builder.AppendLine();
            builder.AppendLine(
                $"- 会话 ID：{_session.SessionId}");
            builder.AppendLine(
                $"- 创建时间：{_session.CreatedAt:yyyy-MM-dd HH:mm}");
            builder.AppendLine(
                $"- 更新时间：{_session.UpdatedAt:yyyy-MM-dd HH:mm}");
            builder.AppendLine($"- 对话轮次：{_session.RoundCount}");
            builder.AppendLine($"- 消息条数：{_session.Messages.Count}");
            builder.AppendLine();
            if (!string.IsNullOrEmpty(_session.AccumulatedSummary))
            {
                builder.AppendLine("## 会话摘要");
                builder.AppendLine();
                builder.AppendLine(_session.AccumulatedSummary);
                builder.AppendLine();
            }

            builder.AppendLine("## 对话记录");
            builder.AppendLine();
            foreach (var message in _session.Messages)
            {
                if (message.Role == "tool")
                {
                    continue;
                }

                var label = message.Role switch
                {
                    "user" => "你",
                    "assistant" => "Revit AI",
                    _ => message.Role
                };
                builder.AppendLine($"### {label}（{message.Timestamp:HH:mm:ss}）");
                builder.AppendLine();
                builder.AppendLine(message.Content ?? string.Empty);
                builder.AppendLine();
                if (message.ToolCalls is { Count: > 0 })
                {
                    builder.AppendLine(
                        "工具调用：" + string.Join(
                            "、",
                            message.ToolCalls.Select(call => call.ToolName)));
                    builder.AppendLine();
                }
            }

            File.WriteAllText(path, builder.ToString());
            return (true, "已导出：" + path);
        }
        catch (Exception ex)
        {
            return (false, "导出失败：" + ex.GetBaseException().Message);
        }
    }

    public JsonObject GetStatus()
    {
        var tools = _tools.Tools.ToList();
        return new JsonObject
        {
            ["configured"] = IsConfigured,
            ["toolsReady"] = tools.Count > 0,
            ["apiUrl"] = ApiUrl,
            ["model"] = Model,
            ["hasApiKey"] = HasApiKey,
            ["historyMessages"] = _session.Messages.Count,
            ["sessionId"] = _session.SessionId,
            ["sessionTitle"] = _session.Title,
            ["rounds"] = _session.RoundCount,
            ["summaryCoveredMessages"] = _session.SummaryMessageCount,
            ["hasSummary"] = !string.IsNullOrEmpty(_session.AccumulatedSummary),
            ["build"] = "v9.2-external-rename",
            ["skills"] = new JsonArray(),
            ["toolCount"] = tools.Count,
            ["toolSets"] = new JsonArray(
                tools
                    .Select(x => x.ToolSet)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .Select(x => JsonValue.Create(x))
                    .ToArray()),
            ["tools"] = new JsonArray(
                tools
                    .OrderBy(x => x.ToolSet, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new JsonObject
                    {
                        ["name"] = x.Name,
                        ["description"] = x.Description,
                        ["toolSet"] = x.ToolSet,
                        ["mutating"] = x.Mutating,
                        ["requiresTransaction"] = x.RequiresTransaction,
                        ["supportsDryRun"] = x.SupportsDryRun
                    })
                    .Cast<JsonNode?>()
                    .ToArray())
        };
    }

    public JsonObject SaveSettings(
        string apiUrl,
        string? apiKey,
        string model)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new ArgumentException("API URL is required.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.");
        }

        _settings.ApiUrl = apiUrl.Trim();
        _settings.Model = model.Trim();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _settings.ProtectedApiKey = _settingsStore.ProtectApiKey(apiKey.Trim());
        }

        _settingsStore.Save(_settings);
        _agentLoop = null;
        return GetStatus();
    }

    public async Task<JsonObject> TestConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "API URL, API key and model must be configured first.");
        }

        using var client = CreateModelClient();
        var response = await client.CreateAsync(
            new AgentModelRequest
            {
                Model = Model,
                Tools = [],
                Messages =
                [
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "Reply with the single word OK."
                    }
                ]
            },
            cancellationToken);

        return new JsonObject
        {
            ["success"] = true,
            ["apiUrl"] = ApiUrl,
            ["model"] = Model,
            ["response"] = response.Content
        };
    }

    public void Dispose()
    {
        _agentLoop?.Dispose();
        _agentLoop = null;
    }

    private AgentLoop CreateAgentLoop()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Model API is not configured. Open API Settings and save "
                + "the URL, API key and model.");
        }

        var systemPrompt =
            "You are the Revit automation assistant inside BIM-AI Studio. "
            + "Use the supplied function tools through standard tool calls. "
            + "Never invent tool results. Resolve family, type, material, level, "
            + "grid and view names through query tools before using them. "
            + "Prefer native modeling tools over generated code. "
            + "Do not use file access, reflection or process APIs from execute_code."
            + Environment.NewLine
            + Environment.NewLine
            + BuildToolPrompt();

        return new AgentLoop(
            CreateModelClient(),
            _tools,
            Model,
            systemPrompt: systemPrompt);
    }

    private string BuildToolPrompt()
    {
        var lines = _tools.Tools
            .OrderBy(tool => tool.ToolSet, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(tool => $"- {tool.Name} [{tool.ToolSet}]: {tool.Description}");
        return "Available Revit tools:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private OpenAiCompatibleModelClient CreateModelClient()
    {
        var apiKey = _settingsStore.UnprotectApiKey(_settings.ProtectedApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("API key is not configured.");
        }

        return new OpenAiCompatibleModelClient(new OpenAiCompatibleOptions
        {
            ApiUrl = ApiUrl,
            ApiKey = apiKey,
            Organization = Environment.GetEnvironmentVariable("REVIT_AI_ORGANIZATION")
        });
    }
}

