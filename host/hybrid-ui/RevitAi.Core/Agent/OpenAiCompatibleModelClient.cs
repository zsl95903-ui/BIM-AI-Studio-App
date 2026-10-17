using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitAi.Core.Agent;

public sealed class OpenAiCompatibleOptions
{
    public required string ApiUrl { get; init; }

    public required string ApiKey { get; init; }

    public string? Organization { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class OpenAiCompatibleModelClient : IAgentModelClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;

    public OpenAiCompatibleModelClient(OpenAiCompatibleOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiUrl))
        {
            throw new ArgumentException("API URL is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("API key is required.", nameof(options));
        }

        _endpoint = BuildEndpoint(options.ApiUrl);
        _httpClient = new HttpClient
        {
            Timeout = options.Timeout
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);

        if (!string.IsNullOrWhiteSpace(options.Organization))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "OpenAI-Organization",
                options.Organization);
        }
    }

    public async Task<AgentModelResponse> CreateAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var messages = request.Messages.Select(x => x.DeepClone().AsObject()).ToArray();
        ApplyAttachments(messages, request.Attachments);

        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(messages.Cast<JsonNode?>().ToArray()),
            ["tools"] = new JsonArray(
                request.Tools.Select(x => x.ToOpenAiTool()).ToArray()),
            ["tool_choice"] = "auto",
            ["stream"] = false
        };

        using var content = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.PostAsync(
            _endpoint,
            content,
            cancellationToken);

#if NET48
        var responseText = await response.Content.ReadAsStringAsync();
#else
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
#endif
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 402
                || responseText.Contains(
                    "Insufficient Balance",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "模型 API 余额不足（402 Insufficient Balance）。"
                    + "请为当前 API Key 充值，或在 API 设置中更换可用 Key。");
            }

            throw new InvalidOperationException(
                $"Model API returned {(int)response.StatusCode}: {responseText}");
        }

        var root = JsonNode.Parse(responseText)?.AsObject()
            ?? throw new InvalidOperationException("Model API returned an empty response.");

        var message = root["choices"]?[0]?["message"]?.AsObject()
            ?? throw new InvalidOperationException("Model API response has no choices[0].message.");

        var contentText = message["content"]?.GetValue<string>();
        var calls = new List<Tools.ToolCall>();
        var toolCalls = message["tool_calls"]?.AsArray();

        if (toolCalls is not null)
        {
            foreach (var item in toolCalls)
            {
                var toolCall = item?.AsObject();
                var function = toolCall?["function"]?.AsObject();
                var id = toolCall?["id"]?.GetValue<string>();
                var name = function?["name"]?.GetValue<string>();
                var argumentsText = function?["arguments"]?.GetValue<string>() ?? "{}";

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                calls.Add(new Tools.ToolCall
                {
                    Id = id,
                    Name = name,
                    Arguments = JsonNode.Parse(argumentsText)?.AsObject() ?? new JsonObject()
                });
            }
        }

        var dsmlCalls = new List<Tools.ToolCall>();
        if (calls.Count == 0
            && !string.IsNullOrWhiteSpace(contentText)
            && TryParseDsmlToolCalls(contentText, request.Tools, out var parsedDsmlCalls))
        {
            dsmlCalls = parsedDsmlCalls;
            calls.AddRange(dsmlCalls);
            contentText = null;
        }

        var rawAssistantMessage = message.DeepClone().AsObject();
        if (dsmlCalls.Count > 0)
        {
            rawAssistantMessage["content"] = null;
            rawAssistantMessage["tool_calls"] = new JsonArray(
                dsmlCalls.Select(call => new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.ToJsonString()
                    }
                }).Cast<JsonNode?>().ToArray());
        }

        return new AgentModelResponse
        {
            RawAssistantMessage = rawAssistantMessage,
            Content = contentText,
            ToolCalls = calls
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static string BuildEndpoint(string apiUrl)
    {
        var trimmed = apiUrl.Trim().TrimEnd('/');

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed}/chat/completions";
    }

    private static void ApplyAttachments(
        IReadOnlyList<JsonObject> messages,
        IReadOnlyList<AgentAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        var userMessage = messages.LastOrDefault(message =>
            string.Equals(
                message["role"]?.GetValue<string>(),
                "user",
                StringComparison.OrdinalIgnoreCase));
        if (userMessage is null)
        {
            return;
        }

        var originalText = userMessage["content"]?.GetValue<string>() ?? string.Empty;
        var attachmentLines = attachments.Select(attachment =>
            $"- {attachment.FileName} ({attachment.ContentType}, {attachment.Size} bytes)"
            + (string.IsNullOrWhiteSpace(attachment.FilePath)
                ? string.Empty
                : $"\n  本地文件路径: {attachment.FilePath}"));
        var text = originalText
            + "\n\n用户附加了以下文件：\n"
            + string.Join("\n", attachmentLines);

        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            }
        };

        foreach (var attachment in attachments.Where(IsSupportedImage))
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = attachment.DataUrl
                }
            });
        }

        foreach (var attachment in attachments.Where(x =>
                     x.IsImage && !IsSupportedImage(x)))
        {
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"图片附件 {attachment.FileName} 格式无效，已忽略。"
            });
        }

        foreach (var attachment in attachments.Where(x =>
                     !string.IsNullOrWhiteSpace(x.ExtractedText)))
        {
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] =
                    $"附件解析：{attachment.FileName}\n"
                    + attachment.ExtractedText
            });
        }

        foreach (var attachment in attachments.Where(x =>
                     !string.IsNullOrWhiteSpace(x.ExtractionError)))
        {
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] =
                    $"附件解析失败：{attachment.FileName}\n"
                    + attachment.ExtractionError
            });
        }

        userMessage["content"] = content;
    }

    private static bool TryParseDsmlToolCalls(
        string content,
        IReadOnlyList<Tools.ToolDefinition> tools,
        out List<Tools.ToolCall> calls)
    {
        calls = [];
        if (!content.Contains("DSML", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

#if NET48
        var normalized = content
            .Replace("\\<", "<")
            .Replace("\\>", ">")
            .Replace("\\｜", "｜")
            .Replace("\\|", "|")
            .Replace("\\\r\n", "\r\n")
            .Replace("\\\n", "\n");
#else
        var normalized = content
            .Replace("\\<", "<", StringComparison.Ordinal)
            .Replace("\\>", ">", StringComparison.Ordinal)
            .Replace("\\｜", "｜", StringComparison.Ordinal)
            .Replace("\\|", "|", StringComparison.Ordinal)
            .Replace("\\\r\n", "\r\n", StringComparison.Ordinal)
            .Replace("\\\n", "\n", StringComparison.Ordinal);
#endif

        var invokePattern =
            @"<[|｜]{2}\s*DSML\s*[|｜]{2}\s*invoke\s+name\s*=\s*[""']([^""']+)[""'][^>]*>(?<body>[\s\S]*?)</[|｜]{2}\s*DSML\s*[|｜]{2}\s*invoke\s*>";
        var parameterPattern =
            @"<[|｜]{2}\s*DSML\s*[|｜]{2}\s*parameter\s+name\s*=\s*[""']([^""']+)[""'][^>]*>(?<value>[\s\S]*?)</[|｜]{2}\s*DSML\s*[|｜]{2}\s*parameter\s*>";

        foreach (Match invoke in Regex.Matches(
                     normalized,
                     invokePattern,
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var rawName = invoke.Groups[1].Value.Trim();
            var name = ResolveToolName(rawName, tools);
            var arguments = new JsonObject();

            foreach (Match parameter in Regex.Matches(
                         invoke.Groups["body"].Value,
                         parameterPattern,
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                arguments[parameter.Groups[1].Value.Trim()] =
                    ParseParameterValue(parameter.Groups["value"].Value.Trim());
            }

            calls.Add(new Tools.ToolCall
            {
                Id = $"dsml_{Guid.NewGuid():N}",
                Name = name,
                Arguments = arguments
            });
        }

        return calls.Count > 0;
    }

    private static bool IsSupportedImage(AgentAttachment attachment)
    {
        if (!attachment.IsImage
            || string.IsNullOrWhiteSpace(attachment.DataUrl)
            || !attachment.DataUrl.StartsWith(
                "data:",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = attachment.DataUrl.IndexOf(',');
        if (commaIndex <= 0)
        {
            return false;
        }

        var header = attachment.DataUrl[..commaIndex];
        var supportedMime = new[]
        {
            "image/png",
            "image/jpeg",
            "image/jpg",
            "image/gif",
            "image/webp"
        }.Any(mime =>
            header.Contains(mime, StringComparison.OrdinalIgnoreCase));
        if (!supportedMime)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(
                attachment.DataUrl[(commaIndex + 1)..]);
            return HasSupportedImageSignature(bytes);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasSupportedImageSignature(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        if (bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
        {
            return true;
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return true;
        }

        if (Encoding.ASCII.GetString(bytes, 0, 6).StartsWith(
                "GIF8",
                StringComparison.Ordinal))
        {
            return true;
        }

        return Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
            && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP";
    }

    private static string ResolveToolName(
        string rawName,
        IReadOnlyList<Tools.ToolDefinition> tools)
    {
        var exact = tools.FirstOrDefault(
            tool => string.Equals(tool.Name, rawName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Name;
        }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["get_levels"] = "get_all_levels",
            ["get_current_view_info"] = "get_active_view",
            ["current_view"] = "get_active_view",
            ["list_levels"] = "get_all_levels"
        };

        if (aliases.TryGetValue(rawName, out var mapped))
        {
            return tools.Any(tool =>
                string.Equals(tool.Name, mapped, StringComparison.OrdinalIgnoreCase))
                ? mapped
                : rawName;
        }

        return rawName;
    }

    private static JsonNode? ParseParameterValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return JsonNode.Parse(value);
        }
        catch
        {
            return value;
        }
    }
}
