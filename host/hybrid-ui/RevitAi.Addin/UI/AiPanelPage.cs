using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RevitAi.Core.Agent;
using RevitAi.Addin.Services;

namespace RevitAi.Addin.UI;

internal sealed class AiPanelPage : Page, IDockablePaneProvider, IDisposable
{
    private readonly WebView2 _webView;
    private readonly List<AgentAttachment> _pendingAttachments = [];
    private CancellationTokenSource? _chatCancellation;
    private CancellationTokenSource? _codexCancellation;
    private Task _chatTask = Task.CompletedTask;
    private int _runSequence;
    private bool _initialized;

    public AiPanelPage()
    {
        _webView = new WebView2();
        Content = new Grid
        {
            Children = { _webView }
        };

        Loaded += async (_, _) => await InitializeWebViewAsync();
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = this;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right
        };
    }

    public void Post(JsonObject message)
    {
        var core = _webView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        // Codex output and other background work post from worker threads, so
        // marshal back to the UI thread that owns the WebView.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Post(message)));
            return;
        }

        core.PostWebMessageAsJson(message.ToJsonString());
    }

    public void Dispose()
    {
        _chatCancellation?.Cancel();
        _chatCancellation?.Dispose();
        _chatCancellation = null;
        _webView.Dispose();
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitAi",
            "WebView2");
        Directory.CreateDirectory(userData);

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userData);
        await _webView.EnsureCoreWebView2Async(environment);

        _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        var html = BuildEmbeddedHtml();
        _webView.NavigateToString(html);

        Post(new JsonObject
        {
            ["type"] = "host.status",
            ["agent"] = AppServices.Agent.GetStatus()
        });
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        _ = HandleWebMessageAsync(e.WebMessageAsJson);
    }

    private async Task HandleWebMessageAsync(string json)
    {
        try
        {
            var message = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            var type = message["type"]?.GetValue<string>();

            switch (type)
            {
                case "chat":
                    await HandleChatAsync(
                        message["text"]?.GetValue<string>() ?? string.Empty);
                    break;

                case "chat.cancel":
                    _chatCancellation?.Cancel();
                    break;

                case "attachment.add":
                    HandleAttachmentAdd(message);
                    break;

                case "attachment.clear":
                    _pendingAttachments.Clear();
                    PostAttachments();
                    break;

                case "get.status":
                    Post(new JsonObject
                    {
                        ["type"] = "host.status",
                        ["agent"] = AppServices.Agent.GetStatus()
                    });
                    break;

                case "reset":
                    await CancelRunningChatAsync();
                    AppServices.Agent.Reset();
                    Post(new JsonObject
                    {
                        ["type"] = "chat.reset"
                    });
                    break;

                case "settings.save":
                    await HandleSaveSettingsAsync(message);
                    break;

                case "settings.test":
                    await HandleTestSettingsAsync();
                    break;

                case "codex.status":
                    PostCodexStatus();
                    break;

                case "codex.mcp.register":
                {
                    var (success, notice) = await CodexIntegration.RegisterMcpAsync();
                    PostCodexNotice(success, notice);
                    break;
                }

                case "codex.mcp.remove":
                {
                    var (success, notice) = await CodexIntegration.UnregisterMcpAsync();
                    PostCodexNotice(success, notice);
                    break;
                }

                case "codex.open":
                {
                    var (success, notice) = CodexIntegration.OpenDesktopApp();
                    PostCodexNotice(success, notice);
                    break;
                }

                case "codex.run":
                    _ = HandleCodexRunAsync(message);
                    break;

                case "codex.cancel":
                    CodexIntegration.Cancel();
                    PostCodexNotice(true, "已请求停止 Codex。");
                    break;

                case "sessions.list":
                    PostSessions("sessions.list");
                    break;

                case "sessions.new":
                    PostSessions(
                        "sessions.updated",
                        "已新建会话。",
                        AppServices.Agent.NewSession());
                    break;

                case "sessions.save":
                    PostSessions(
                        "sessions.updated",
                        "已保存当前会话。",
                        AppServices.Agent.SaveCurrentSession());
                    break;

                case "sessions.open":
                    PostSessions(
                        "sessions.updated",
                        "已打开会话。",
                        AppServices.Agent.OpenSession(
                            message["sessionId"]?.GetValue<string>() ?? string.Empty));
                    break;

                case "sessions.delete":
                    PostSessions(
                        "sessions.updated",
                        "已删除会话。",
                        AppServices.Agent.DeleteSession(
                            message["sessionId"]?.GetValue<string>() ?? string.Empty));
                    break;

                case "sessions.export":
                {
                    var (success, notice) = AppServices.Agent.ExportCurrentSession();
                    Post(new JsonObject
                    {
                        ["type"] = "sessions.notice",
                        ["success"] = success,
                        ["message"] = notice
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Post(new JsonObject
            {
                ["type"] = "chat.error",
                ["message"] = ex.GetBaseException().Message
            });
        }
    }

    private async Task HandleChatAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var attachments = _pendingAttachments.ToArray();
        _pendingAttachments.Clear();
        PostAttachments();

        // Only one agent run may be active at a time: cancel whatever is still
        // running and wait for it to unwind before starting the new prompt.
        await CancelRunningChatAsync();

        var cancellation = new CancellationTokenSource();
        _chatCancellation = cancellation;
        var runId = Interlocked.Increment(ref _runSequence);
        var run = RunChatCoreAsync(
            prompt,
            runId,
            attachments,
            cancellation.Token);
        _chatTask = run;
        try
        {
            await run;
        }
        finally
        {
            if (ReferenceEquals(_chatCancellation, cancellation))
            {
                _chatCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task CancelRunningChatAsync()
    {
        _chatCancellation?.Cancel();
        var running = _chatTask;
        if (running.IsCompleted)
        {
            return;
        }

        try
        {
            await running;
        }
        catch
        {
            // The run reports its own outcome to the panel.
        }
    }

    private async Task RunChatCoreAsync(
        string prompt,
        int runId,
        IReadOnlyList<AgentAttachment> attachments,
        CancellationToken cancellationToken)
    {
        Post(new JsonObject
        {
            ["type"] = "chat.started",
            ["runId"] = runId
        });

        Post(new JsonObject
        {
            ["type"] = "chat.user",
            ["runId"] = runId,
            ["message"] = prompt
        });

        try
        {
            var result = await AppServices.Agent.RunAsync(
                prompt,
                cancellationToken,
                progress: progressEvent =>
                {
                    var message = new JsonObject
                    {
                        ["type"] = "chat.progress",
                        ["runId"] = runId,
                        ["kind"] = progressEvent.Kind,
                        ["round"] = progressEvent.Round,
                        ["message"] = progressEvent.Message
                    };

                    if (progressEvent.ToolCall is not null)
                    {
                        message["tool"] = new JsonObject
                        {
                            ["id"] = progressEvent.ToolCall.Id,
                            ["name"] = progressEvent.ToolCall.Name,
                            ["arguments"] = progressEvent.ToolCall.Arguments.DeepClone()
                        };
                    }

                    if (progressEvent.ToolResult is not null)
                    {
                        message["result"] = new JsonObject
                        {
                            ["success"] = progressEvent.ToolResult.Success,
                            ["payload"] = progressEvent.ToolResult.Payload.DeepClone(),
                            ["errorCode"] = progressEvent.ToolResult.ErrorCode,
                            ["errorMessage"] = progressEvent.ToolResult.ErrorMessage,
                            ["durationMs"] = progressEvent.ToolResult.Duration.TotalMilliseconds
                        };
                    }

                    Post(message);
                    return Task.CompletedTask;
                },
                attachments: attachments);

            Post(new JsonObject
            {
                ["type"] = "chat.toolResults",
                ["runId"] = runId,
                ["items"] = new JsonArray(
                    result.ToolResults.Select(item => new JsonObject
                    {
                        ["toolCallId"] = item.ToolCallId,
                        ["toolName"] = item.ToolName,
                        ["success"] = item.Success,
                        ["durationMs"] = item.Duration.TotalMilliseconds,
                        ["payload"] = item.Payload.DeepClone(),
                        ["errorCode"] = item.ErrorCode,
                        ["errorMessage"] = item.ErrorMessage
                    }).Cast<JsonNode?>().ToArray())
            });

            Post(new JsonObject
            {
                ["type"] = "chat.response",
                ["runId"] = runId,
                ["message"] = result.FinalText
            });
        }
        catch (OperationCanceledException)
        {
            Post(new JsonObject
            {
                ["type"] = "chat.cancelled",
                ["runId"] = runId
            });
        }
        catch (Exception ex)
        {
            Post(new JsonObject
            {
                ["type"] = "chat.error",
                ["runId"] = runId,
                ["message"] = ex.GetBaseException().Message
            });
        }
        finally
        {
            Post(new JsonObject
            {
                ["type"] = "chat.finished",
                ["runId"] = runId
            });
        }
    }

    private void HandleAttachmentAdd(JsonObject message)
    {
        var fileName = message["fileName"]?.GetValue<string>()?.Trim();
        var contentType = message["contentType"]?.GetValue<string>()?.Trim();
        var dataUrl = message["dataUrl"]?.GetValue<string>();
        var size = message["size"]?.GetValue<long>() ?? 0;

        if (string.IsNullOrWhiteSpace(fileName)
            || string.IsNullOrWhiteSpace(contentType)
            || string.IsNullOrWhiteSpace(dataUrl))
        {
            throw new InvalidOperationException(
                "附件缺少文件名、类型或内容。");
        }

        const long maxSize = 50L * 1024L * 1024L;
        if (size > maxSize)
        {
            throw new InvalidOperationException(
                $"附件过大：{fileName}。单个文件最大 50 MB。");
        }

        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"附件内容格式无效：{fileName}。");
        }

        var attachmentId = Guid.NewGuid().ToString("N");
        var commaIndex = dataUrl.IndexOf(',');
        var rawBase64 = commaIndex >= 0
            ? dataUrl[(commaIndex + 1)..]
            : dataUrl;
        var storageDirectory = Path.Combine(
            Path.GetTempPath(),
            "RevitAi",
            "attachments");
        Directory.CreateDirectory(storageDirectory);
        var safeName = string.Join(
            "_",
            fileName.Split(Path.GetInvalidFileNameChars()));
        var filePath = Path.Combine(
            storageDirectory,
            $"{attachmentId}_{safeName}");
        File.WriteAllBytes(filePath, Convert.FromBase64String(rawBase64));

        _pendingAttachments.Add(new AgentAttachment
        {
            AttachmentId = attachmentId,
            FileName = fileName,
            ContentType = contentType,
            DataUrl = dataUrl,
            Size = size,
            FilePath = filePath
        });

        PostAttachments();
    }

    private void PostSessions(string type, string? message = null, JsonObject? agent = null)
    {
        Post(new JsonObject
        {
            ["type"] = type,
            ["agent"] = agent ?? AppServices.Agent.GetStatus(),
            ["items"] = AppServices.Agent.ListSessions(),
            ["message"] = message
        });
    }

    private void PostCodexStatus()
    {
        Post(new JsonObject
        {
            ["type"] = "codex.status",
            ["status"] = CodexIntegration.GetStatus(),
            ["workspace"] = CodexIntegration.GetWorkspaceDirectory()
        });
    }

    private void PostCodexNotice(bool success, string message)
    {
        Post(new JsonObject
        {
            ["type"] = "codex.notice",
            ["success"] = success,
            ["message"] = message,
            ["status"] = CodexIntegration.GetStatus()
        });
    }

    private async Task HandleCodexRunAsync(JsonObject message)
    {
        var prompt = message["prompt"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            PostCodexNotice(false, "任务内容为空。");
            return;
        }

        var autoApprove = message["autoApprove"]?.GetValue<bool>() ?? false;
        _codexCancellation?.Dispose();
        _codexCancellation = new CancellationTokenSource();
        Post(new JsonObject
        {
            ["type"] = "codex.started",
            ["status"] = CodexIntegration.GetStatus()
        });

        try
        {
            var exitCode = await CodexIntegration.RunAsync(
                prompt,
                autoApprove,
                line => Post(new JsonObject
                {
                    ["type"] = "codex.event",
                    ["line"] = line
                }),
                _codexCancellation.Token);
            Post(new JsonObject
            {
                ["type"] = "codex.exit",
                ["exitCode"] = exitCode,
                ["status"] = CodexIntegration.GetStatus()
            });
        }
        catch (Exception ex)
        {
            PostCodexNotice(false, ex.GetBaseException().Message);
        }
        finally
        {
            _codexCancellation?.Dispose();
            _codexCancellation = null;
        }
    }

    // Returns null when the field is absent so the stored value is kept; an
    // empty string deliberately clears the slot.
    private static string? TryReadText(JsonObject message, string name)
    {
        if (message[name] is not JsonValue value
            || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        return text;
    }

    private void PostAttachments()
    {
        Post(new JsonObject
        {
            ["type"] = "chat.attachments",
            ["items"] = new JsonArray(
                _pendingAttachments.Select(item => new JsonObject
                {
                    ["fileName"] = item.FileName,
                    ["contentType"] = item.ContentType,
                    ["size"] = item.Size
                }).Cast<JsonNode?>().ToArray())
        });
    }

    private Task HandleSaveSettingsAsync(JsonObject message)
    {
        var status = AppServices.Agent.SaveSettings(
            message["apiUrl"]?.GetValue<string>() ?? string.Empty,
            message["apiKey"]?.GetValue<string>(),
            message["model"]?.GetValue<string>() ?? string.Empty);

        Post(new JsonObject
        {
            ["type"] = "settings.saved",
            ["agent"] = status
        });

        return Task.CompletedTask;
    }

    private async Task HandleTestSettingsAsync()
    {
        Post(new JsonObject
        {
            ["type"] = "settings.testing"
        });

        try
        {
            var result = await AppServices.Agent.TestConnectionAsync();
            Post(new JsonObject
            {
                ["type"] = "settings.tested",
                ["result"] = result,
                ["agent"] = AppServices.Agent.GetStatus()
            });
        }
        catch (Exception ex)
        {
            Post(new JsonObject
            {
                ["type"] = "settings.error",
                ["message"] = ex.GetBaseException().Message
            });
        }
    }

    private static string BuildEmbeddedHtml()
    {
        var html = ReadEmbeddedText("RevitAi.Addin.UI.index.html");
        var css = ReadEmbeddedText("RevitAi.Addin.UI.styles.css");
        var script = ReadEmbeddedText("RevitAi.Addin.UI.app.js");

        html = Regex.Replace(
            html,
            """<link\s+rel="stylesheet"\s+href="[^"]+"\s*/?>""",
            $"<style>{css}</style>",
            RegexOptions.IgnoreCase);
        html = Regex.Replace(
            html,
            """<script\s+src="[^"]+"></script>""",
            $"<script>{script}</script>",
            RegexOptions.IgnoreCase);

        return html;
    }

    private static string ReadEmbeddedText(string name)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded UI resource '{name}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
