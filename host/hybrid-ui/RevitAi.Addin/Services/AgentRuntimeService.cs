using System.Text.Json.Nodes;
using RevitAi.Core.AI;
using RevitAi.Abstractions.AI;
using RevitAi.Abstractions.Loader;
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

        // Previous behaviour: the session is persisted after every turn.
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

    private static JsonArray BuildSkillStatus()
    {
        var skills = new JsonArray();
        var manager = ServiceProvider.GetService<ISkillManager>();
        if (manager is null)
        {
            return skills;
        }

        var list = manager.GetSkillList();
        if (!list.IsSuccess || list.Value is null)
        {
            return skills;
        }

        foreach (var skill in list.Value)
        {
            var variables = new JsonArray();
            foreach (var variable in skill.Variables ?? [])
            {
                variables.Add(variable.Name);
            }

            skills.Add(new JsonObject
            {
                ["name"] = skill.SkillName,
                ["displayName"] = skill.DisplayName,
                ["description"] = skill.Description,
                ["category"] = skill.Category,
                ["steps"] = skill.Steps?.Count ?? 0,
                ["variables"] = variables
            });
        }

        return skills;
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
            ["skills"] = BuildSkillStatus(),
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

        var registry = AIToolRegistry.Instance;
        var promptManager = new AISystemPromptManager(registry);
        var systemPrompt = promptManager.GenerateSystemPrompt()
            + Environment.NewLine
            + "当工具可用时，优先使用标准 tool_calls。若模型接口返回文本工具调用，"
            + "系统会兼容解析，但不要伪造工具结果。"
            + Environment.NewLine
            + "名称解析规则：族名、类型名、材质名、标高名、轴网名、视图名等必须先通过查询工具"
            + "（get_all_families / get_family_types / material_query / get_all_levels / get_all_grids / get_all_views）"
            + "取得真实值，禁止凭印象猜测。若用户提到的名称不存在，先列出可用项并选择最接近的再执行。"
            + Environment.NewLine
            + "创建结构柱、梁、墙、门窗等构件时优先复用项目中已有的族，不要从族模板新建族；"
            + "只有用户明确要求自定义族时才处理族模板。"
            + Environment.NewLine            + "Revit 2027 API 兼容要求：ElementId 已没有 IntegerValue 属性，"
            + "读取整数 ID 必须使用 ElementId.Value（long）。"
            + "标高和轴网显示优先调用 create_level/create_grid，插件会自动处理"
            + "立面标高线长度、两端标记、轴网红色和双端轴号。"
            + "只有现有工具无法完成的显示细节才使用 execute_code。"
            + "在立面或剖面中设置标高线时使用"
            + " level.SetCurveInView(DatumExtentType.ViewSpecific, view, line)、"
            + " level.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.ViewSpecific)"
            + " 和 level.ShowBubbleInView(DatumEnds.End0, view)。"
            + "在平面中设置轴网时使用 OverrideGraphicSettings."
            + "SetProjectionLineColor(new Color(255, 0, 0))，不要假设创建基准元素"
            + "本身会改变视图显示。"
            + "execute_code 里不要写裸表达式语句（例如单独一行 g.Name; 或多行"
            + "属性链末尾的 .Name;），会触发 CS0201 编译错误；取值请赋给变量，"
            + "需要返回结果时以 new { ... } 表达式结尾。"
            + "execute_code 里也不要使用 AppDomain、反射或文件读写等被安全策略"
            + "禁止的 API。";

        systemPrompt += Environment.NewLine
            + "Grid/level display (red colour, bubbles on both ends, extended "
            + "datum lines) is applied automatically by the add-in after "
            + "create_grid or execute_code. Do not hand-write bubble, extent "
            + "or override code in execute_code.";

        return new AgentLoop(
            CreateModelClient(),
            _tools,
            Model,
            systemPrompt: systemPrompt,
            specialCommandHandler: HandleSpecialCommandAsync);
    }

    // Port of the previous HandleSpecialCommandAsync / HandleRequestSkillList /
    // HandleRequestSkillDetail: the loop intercepts these commands, injects a
    // message and retries the round.
    private Task<AgentSpecialCommandResult?> HandleSpecialCommandAsync(
        string content,
        CancellationToken cancellationToken)
    {
        if (!SpecialCommandHandler.IsSpecialCommand(content))
        {
            return Task.FromResult<AgentSpecialCommandResult?>(null);
        }

        var (command, argument) = SpecialCommandHandler.ParseSpecialCommand(content);
        switch (command)
        {
            case "REQUEST_TOOLS":
                return Task.FromResult<AgentSpecialCommandResult?>(
                    new AgentSpecialCommandResult
                    {
                        SystemPrompt = new AISystemPromptManager(
                            AIToolRegistry.Instance).GenerateSystemPromptWithTools(),
                        MessageRole = "system",
                        MessageContent = "工具定义已自动提供，无需再次请求。"
                    });

            case "REQUEST_SKILL_LIST":
            {
                var manager = ServiceProvider.GetService<ISkillManager>();
                if (manager is null)
                {
                    return Task.FromResult<AgentSpecialCommandResult?>(
                        new AgentSpecialCommandResult
                        {
                            MessageRole = "system",
                            MessageContent = "技能系统未注册。"
                        });
                }

                var list = manager.GetSkillList();
                if (!list.IsSuccess || list.Value is null)
                {
                    return Task.FromResult<AgentSpecialCommandResult?>(
                        new AgentSpecialCommandResult
                        {
                            MessageRole = "system",
                            MessageContent = "技能列表读取失败：" + (list.Error ?? "未知错误")
                        });
                }

                var lines = list.Value.Select(skill =>
                    $"- {skill.SkillName}（{skill.DisplayName}）：{skill.Description}");
                return Task.FromResult<AgentSpecialCommandResult?>(
                    new AgentSpecialCommandResult
                    {
                        MessageRole = "system",
                        MessageContent =
                            $"可用技能列表（共 {list.Value.Count} 个）：\n"
                            + string.Join("\n", lines)
                            + "\n\n使用 execute_skill 工具执行技能。"
                    });
            }

            case "REQUEST_SKILL_DETAIL":
            {
                var manager = ServiceProvider.GetService<ISkillManager>();
                if (manager is null)
                {
                    return Task.FromResult<AgentSpecialCommandResult?>(
                        new AgentSpecialCommandResult
                        {
                            MessageRole = "system",
                            MessageContent = "技能系统未注册。"
                        });
                }

                var detail = manager.GetSkillDetail(argument ?? string.Empty);
                if (!detail.IsSuccess || detail.Value is null)
                {
                    return Task.FromResult<AgentSpecialCommandResult?>(
                        new AgentSpecialCommandResult
                        {
                            MessageRole = "system",
                            MessageContent =
                                $"技能 '{argument}' 不存在。"
                        });
                }

                var steps = detail.Value.Steps is null
                    ? string.Empty
                    : string.Join(
                        "\n",
                        detail.Value.Steps.Select(
                            (step, index) =>
                                $"{index + 1}. {step.Tool}（{step.Description}）"));
                return Task.FromResult<AgentSpecialCommandResult?>(
                    new AgentSpecialCommandResult
                    {
                        MessageRole = "system",
                        MessageContent =
                            $"技能详情：{detail.Value.DisplayName}\n"
                            + $"描述：{detail.Value.Description}\n"
                            + $"步骤：\n{steps}\n\n使用 execute_skill 工具执行此技能。"
                    });
            }
        }

        return Task.FromResult<AgentSpecialCommandResult?>(null);
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

