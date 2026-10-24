using System.Text;
using System.Text.Json.Nodes;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Agent;

public sealed class AgentSpecialCommandResult
{
    public string? SystemPrompt { get; init; }

    public IReadOnlyList<ToolDefinition>? Tools { get; init; }

    // Optional message injected by a host-specific command handler.
    public string? MessageRole { get; init; }

    public string? MessageContent { get; init; }
}

// The agent pipeline:
//   1. system prompt + history (compressed to first 50 + summary + last 50
//      once the session exceeds 100 messages) + the new user message,
//   2. one model call per round with the tool definitions,
//   3. every tool call of a round is executed and answered before the next
//      model call,
//   4. special commands (REQUEST_TOOLS / REQUEST_SKILL_LIST / ...) are handled
//      and the round is repeated,
//   5. the full turn is persisted into the session (tool messages included).
public sealed class AgentLoop : IDisposable
{
    private readonly IAgentModelClient _modelClient;
    private readonly IToolInvoker _tools;
    private readonly string _model;
    private readonly int _maxRounds;
    private readonly string _systemPrompt;
    private readonly Func<string, CancellationToken, Task<AgentSpecialCommandResult?>>?
        _specialCommandHandler;

    public AgentLoop(
        IAgentModelClient modelClient,
        IToolInvoker tools,
        string model,
        int maxRounds = 100,
        string? systemPrompt = null,
        Func<string, CancellationToken, Task<AgentSpecialCommandResult?>>?
            specialCommandHandler = null)
    {
        _modelClient = modelClient;
        _tools = tools;
        _model = model;
        _maxRounds = maxRounds;
        _systemPrompt = systemPrompt ?? DefaultSystemPrompt;
        _specialCommandHandler = specialCommandHandler;
    }

    public async Task<AgentTurnResult> RunAsync(
        IReadOnlyList<JsonObject> history,
        string userPrompt,
        CancellationToken cancellationToken = default,
        Func<AgentProgressEvent, Task>? progress = null,
        IReadOnlyList<AgentAttachment>? attachments = null)
    {
        // Back-compat path used by tests and simple callers: wrap the flat
        // history in a session so the same loop code runs.
        var session = new AgentSessionState();
        foreach (var message in history)
        {
            var role = message["role"]?.GetValue<string>();
            var content = message["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            session.Messages.Add(new AgentSessionMessage
            {
                Role = role,
                Content = content
            });
        }

        return await RunAsync(
            session,
            userPrompt,
            cancellationToken,
            progress,
            attachments);
    }

    public async Task<AgentTurnResult> RunAsync(
        AgentSessionState session,
        string userPrompt,
        CancellationToken cancellationToken = default,
        Func<AgentProgressEvent, Task>? progress = null,
        IReadOnlyList<AgentAttachment>? attachments = null)
    {
        var tools = _tools.Tools;
        string? targetDocument = null;
        if (tools.Any(t => t.Name == "get_document_snapshot"))
        {
            var snapshot = await _tools.InvokeAsync(new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = "get_document_snapshot", Arguments = new() }, cancellationToken);
            if (!snapshot.Success) throw new InvalidOperationException(snapshot.ErrorMessage);
            targetDocument = snapshot.Payload["documentGuid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(targetDocument)) throw new InvalidOperationException("Cannot bind this run to a document.");
        }
        var systemPrompt = _systemPrompt;
        var conversation = new List<JsonObject>
        {
            new()
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            }
        };

        BuildHistory(session, conversation);

        conversation.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userPrompt
        });
        var turnStartIndex = conversation.Count - 1;

        var toolResults = new List<ToolInvocationResult>();
        var round = 0;
        var finalText = string.Empty;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                round++;
                if (round > _maxRounds)
                {
                    finalText =
                        $"任务已达到 {_maxRounds} 轮工具调用上限，尚未全部完成。"
                        + "已完成的操作以上方工具结果为准确认；如需继续，请再次发送“继续执行”。";
                    break;
                }

                if (progress is not null)
                {
                    await progress(new AgentProgressEvent
                    {
                        Kind = "round_started",
                        Round = round,
                        Message = round == 1
                            ? "正在分析请求并选择工具..."
                            : "正在根据工具结果继续分析..."
                    });
                }

                var response = await _modelClient.CreateAsync(
                    new AgentModelRequest
                    {
                        Messages = conversation
                            .Select(message => message.DeepClone().AsObject())
                            .ToList(),
                        Tools = tools,
                        Model = _model,
                        Attachments = round == 1
                            ? attachments ?? Array.Empty<AgentAttachment>()
                            : Array.Empty<AgentAttachment>()
                    },
                    cancellationToken);

                var content = response.Content ?? string.Empty;
                if (string.IsNullOrEmpty(content) && response.ToolCalls.Count == 0)
                {
                    finalText = "抱歉，AI 服务返回了空响应。请稍后再试。";
                    break;
                }

                if (response.ToolCalls.Count > 0)
                {
                    await ExecuteToolCallsAsync(
                        response.ToolCalls,
                        targetDocument,
                        content,
                        conversation,
                        toolResults,
                        cancellationToken,
                        progress);
                    session.RoundCount = round + 1;
                    session.UpdatedAt = DateTime.Now;
                    continue;
                }

                if (_specialCommandHandler is not null
                    && !string.IsNullOrWhiteSpace(content))
                {
                    var commandResult = await _specialCommandHandler(
                        content.Trim(),
                        cancellationToken);
                    if (commandResult is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(commandResult.SystemPrompt)
                            && !string.Equals(
                                commandResult.SystemPrompt,
                                systemPrompt,
                                StringComparison.Ordinal))
                        {
                            systemPrompt = commandResult.SystemPrompt!;
                            conversation[0] = new JsonObject
                            {
                                ["role"] = "system",
                                ["content"] = systemPrompt
                            };
                        }

                        if (commandResult.Tools is { Count: > 0 })
                        {
                            tools = commandResult.Tools;
                        }

                        if (!string.IsNullOrWhiteSpace(commandResult.MessageContent))
                        {
                            conversation.Add(new JsonObject
                            {
                                ["role"] = commandResult.MessageRole ?? "system",
                                ["content"] = commandResult.MessageContent
                            });
                        }

                        continue;
                    }
                }

                finalText = content;
                break;
            }

        }
        finally { PersistTurn(session, conversation, turnStartIndex, finalText); }

        if (progress is not null)
        {
            await progress(new AgentProgressEvent
            {
                Kind = "completed",
                Round = round,
                FinalText = finalText
            });
        }

        return new AgentTurnResult
        {
            FinalText = finalText,
            ToolResults = toolResults,
            Rounds = round
        };
    }

    public void Dispose()
    {
        if (_modelClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    // Keep the first 50 and the last 50 messages, and compress
    // everything between them into one accumulated summary.
    private static void BuildHistory(
        AgentSessionState session,
        List<JsonObject> conversation)
    {
        var history = session.Messages;
        if (history.Count == 0)
        {
            return;
        }

        IReadOnlyList<AgentSessionMessage> window = history;
        if (history.Count > AgentHistorySummarizer.CompressionThreshold)
        {
            var keep = AgentHistorySummarizer.KeepRecentCount;
            var middleCount = history.Count - (keep * 2);
            if (session.SummaryMessageCount < middleCount
                || string.IsNullOrEmpty(session.AccumulatedSummary))
            {
                var summary = AgentHistorySummarizer.SummarizeRange(
                    history,
                    keep,
                    middleCount);
                if (!string.IsNullOrEmpty(summary))
                {
                    session.AccumulatedSummary = summary;
                    session.SummaryMessageCount = middleCount;
                }
            }

            window = history
                .Take(keep)
                .Concat(history.Skip(history.Count - keep))
                .ToList();
            if (!string.IsNullOrEmpty(session.AccumulatedSummary))
            {
                conversation.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] =
                        $"## 📝 会话摘要（{session.SummaryMessageCount} 条历史对话的压缩内容）"
                        + $"\n\n{session.AccumulatedSummary}"
                        + "\n\n---\n以上是会话中间内容的累积摘要，以下是完整的对话。"
                });
            }
        }

        // save_memory results stay in the history; other tool messages are
        // dropped because the tool call itself is not replayed to the model.
        var memoryCallIds = window
            .Where(message => message.ToolCalls is { Count: > 0 })
            .SelectMany(message => message.ToolCalls!)
            .Where(call =>
                string.Equals(call.ToolName, "save_memory", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(call.CallId))
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);

        for (var index = 0; index < window.Count; index++)
        {
            var message = window[index];
            if (message.Role is "system" or "tool")
            {
                continue;
            }

            var content = message.Content ?? string.Empty;
            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                var builder = new StringBuilder();
                if (!string.IsNullOrEmpty(content))
                {
                    builder.AppendLine(content);
                }

                var next = index + 1;
                for (; next < window.Count && window[next].Role == "tool"; next++)
                {
                    var toolMessage = window[next];
                    if (!string.IsNullOrEmpty(toolMessage.ToolCallId)
                        && memoryCallIds.Contains(toolMessage.ToolCallId!))
                    {
                        builder.AppendLine(toolMessage.Content ?? string.Empty);
                    }
                }

                index = next - 1;
                content = builder.ToString().TrimEnd();
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }
            }

            conversation.Add(new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = content
            });
        }
    }

    private async Task ExecuteToolCallsAsync(
        IReadOnlyList<ToolCall> calls,
        string? targetDocument,
        string assistantContent,
        List<JsonObject> conversation,
        List<ToolInvocationResult> results,
        CancellationToken cancellationToken,
        Func<AgentProgressEvent, Task>? progress)
    {
        var toolCallNodes = new JsonArray();
        var toolMessages = new List<JsonObject>();

        try
        {
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (progress is not null)
                {
                    await progress(new AgentProgressEvent
                    {
                        Kind = "tool_started",
                        ToolCall = call,
                        Message = $"正在执行 {call.Name}..."
                    });
                }

                call.ExpectedDocumentGuid = targetDocument;
                var result = await _tools.InvokeAsync(call, cancellationToken);
                results.Add(result);

                if (progress is not null)
                {
                    await progress(new AgentProgressEvent
                    {
                        Kind = "tool_completed",
                        ToolCall = call,
                        ToolResult = result,
                        Message = result.Success
                            ? $"{call.Name} 已完成"
                            : $"{call.Name} 执行失败"
                    });
                }

                toolCallNodes.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.ToJsonString()
                    }
                });
                toolMessages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = ToolResultFormatter.Format(result)
                });
            }

        }
        finally
        {
            conversation.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrWhiteSpace(assistantContent)
                    ? null
                    : assistantContent,
                ["tool_calls"] = toolCallNodes
            });
            conversation.AddRange(toolMessages);
        }
    }

    // Original PersistFullTurnToSession: the whole turn (user message, every
    // assistant/tool exchange and the final answer) is appended to the session.
    private static void PersistTurn(
        AgentSessionState session,
        IReadOnlyList<JsonObject> conversation,
        int turnStartIndex,
        string finalText)
    {
        if (turnStartIndex < 0 || turnStartIndex >= conversation.Count)
        {
            return;
        }

        var added = 0;
        for (var index = turnStartIndex; index < conversation.Count; index++)
        {
            var message = conversation[index];
            var role = message["role"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            session.Messages.Add(new AgentSessionMessage
            {
                Role = role,
                Content = message["content"]?.GetValue<string>(),
                ToolCallId = message["tool_call_id"]?.GetValue<string>(),
                ToolCalls = ReadToolCalls(message["tool_calls"] as JsonArray)
            });
            added++;
        }

        if (!string.IsNullOrEmpty(finalText))
        {
            session.Messages.Add(new AgentSessionMessage
            {
                Role = "assistant",
                Content = finalText
            });
            added++;
        }

        if (added > 0)
        {
            session.UpdatedAt = DateTime.Now;
            if (session.Title == "新对话")
            {
                var firstUser = session.Messages
                    .FirstOrDefault(message => message.Role == "user")
                    ?.Content;
                if (!string.IsNullOrWhiteSpace(firstUser))
                {
                    session.Title = firstUser!.Length > 24
                        ? firstUser[..24]
                        : firstUser;
                }
            }
        }
    }

    private static List<AgentToolCallRecord>? ReadToolCalls(JsonArray? array)
    {
        if (array is null || array.Count == 0)
        {
            return null;
        }

        var calls = new List<AgentToolCallRecord>();
        foreach (var item in array)
        {
            var function = item?["function"];
            var name = function?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            calls.Add(new AgentToolCallRecord
            {
                CallId = item?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                ToolName = name!,
                ArgumentsJson = function?["arguments"]?.GetValue<string>()
            });
        }

        return calls.Count == 0 ? null : calls;
    }

    private const string DefaultSystemPrompt =
        """
        You control Autodesk Revit through local tools.
        Rules:
        1. Never invent element IDs, type IDs, levels, views, or model state.
        2. Query the live Revit document before making assumptions.
        3. Use millimetres at the tool boundary.
        4. Prefer small, verifiable steps.
        5. For mutations, use the requested tool rather than inventing source code.
        6. Do not claim an operation succeeded until the tool result confirms it.
        7. Do not bypass application, filesystem, or transaction safety controls.
        """;
}
