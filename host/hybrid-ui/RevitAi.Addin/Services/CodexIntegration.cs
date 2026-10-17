using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace RevitAi.Addin.Services;

// Manual (user triggered) bridge to Codex. Nothing here is called from the
// agent loop: the panel opens this drawer, the user starts a run, and the
// output is streamed back. The MCP registration makes the Revit tools
// available to that Codex session through RevitAi.McpServer.exe.
internal static class CodexIntegration
{
    private const string McpServerName = "revit";
    private static readonly object Sync = new();
    private static Process? _currentRun;

    public static string McpServerPath => Path.Combine(
        AppContext.BaseDirectory,
        "McpServer",
        "RevitAi.McpServer.exe");

    public static JsonObject GetStatus()
    {
        var codexPath = FindCodexExecutable();
        var mcpPath = McpServerPath;
        var mcpExists = File.Exists(mcpPath);
        var registered = codexPath is not null
            && mcpExists
            && IsMcpRegistered(codexPath);
        bool running;
        lock (Sync)
        {
            running = _currentRun is { HasExited: false };
        }

        return new JsonObject
        {
            ["available"] = codexPath is not null,
            ["codexPath"] = codexPath,
            ["mcpServerPath"] = mcpPath,
            ["mcpServerExists"] = mcpExists,
            ["mcpRegistered"] = registered,
            ["running"] = running
        };
    }

    public static string GetWorkspaceDirectory()
    {
        try
        {
            var path = AppServices.UiApplication?
                .ActiveUIDocument?.Document?.PathName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }
        }
        catch
        {
            // Fall through to the scratch workspace.
        }

        var fallback = Path.Combine(
            Path.GetTempPath(),
            "RevitAi",
            "codex-workspace");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static async Task<(bool Success, string Message)> RegisterMcpAsync()
    {
        var codex = FindCodexExecutable();
        if (codex is null)
        {
            return (false, "未找到 codex 可执行文件（请先安装 Codex）。");
        }

        if (!File.Exists(McpServerPath))
        {
            return (false, $"未找到 MCP 服务：{McpServerPath}");
        }

        if (IsMcpRegistered(codex))
        {
            return (true, "Revit MCP 已注册，无需重复添加。");
        }

        var result = await RunCommandAsync(
            codex,
            ["mcp", "add", McpServerName, "--", McpServerPath],
            TimeSpan.FromSeconds(60));
        return (
            result.ExitCode == 0,
            result.ExitCode == 0
                ? "已注册到 Codex：revit"
                : $"注册失败：{FirstLine(result.Output)}");
    }

    public static async Task<(bool Success, string Message)> UnregisterMcpAsync()
    {
        var codex = FindCodexExecutable();
        if (codex is null)
        {
            return (false, "未找到 codex 可执行文件。");
        }

        var result = await RunCommandAsync(
            codex,
            ["mcp", "remove", McpServerName],
            TimeSpan.FromSeconds(60));
        return (
            result.ExitCode == 0,
            result.ExitCode == 0
                ? "已从 Codex 移除：revit"
                : $"移除失败：{FirstLine(result.Output)}");
    }

    public static (bool Success, string Message) OpenDesktopApp()
    {
        var codex = FindCodexExecutable();
        if (codex is null)
        {
            return (false, "未找到 codex 可执行文件。");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = codex,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("app");
            startInfo.ArgumentList.Add(GetWorkspaceDirectory());
            Process.Start(startInfo);
            return (true, "已请求打开 Codex 桌面端。");
        }
        catch (Exception ex)
        {
            return (false, $"打开 Codex 失败：{ex.GetBaseException().Message}");
        }
    }

    public static bool Cancel()
    {
        lock (Sync)
        {
            if (_currentRun is not { HasExited: false } process)
            {
                return false;
            }

            try
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<int> RunAsync(
        string prompt,
        bool autoApprove,
        Action<string> onEvent,
        CancellationToken cancellationToken)
    {
        var codex = FindCodexExecutable();
        if (codex is null)
        {
            onEvent("未找到 codex 可执行文件（请先安装 Codex）。");
            return -1;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = codex,
            WorkingDirectory = GetWorkspaceDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        if (autoApprove)
        {
            // Codex blocks MCP tool calls unless approvals are granted; the
            // user opts into this per run from the panel.
            startInfo.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
        }

        startInfo.ArgumentList.Add("-");

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                onEvent(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                onEvent("! " + args.Data);
            }
        };

        lock (Sync)
        {
            _currentRun = process;
        }

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Cancel();
            }

            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex)
        {
            onEvent("! 启动 Codex 失败：" + ex.GetBaseException().Message);
            return -1;
        }
        finally
        {
            lock (Sync)
            {
                if (ReferenceEquals(_currentRun, process))
                {
                    _currentRun = null;
                }
            }
        }
    }

    private static bool IsMcpRegistered(string codexPath)
    {
        try
        {
            var result = RunCommandAsync(
                codexPath,
                ["mcp", "get", McpServerName],
                TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (-1, "命令超时。");
        }

        var output = (await stdout) + (await stderr);
        return (process.ExitCode, output.Trim());
    }

    private static string? FindCodexExecutable()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI",
                "Codex",
                "bin");
            if (Directory.Exists(root))
            {
                var candidate = Directory
                    .EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    return candidate.FullName;
                }
            }
        }
        catch
        {
            // Fall through to PATH lookup.
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in new[] { "codex.exe", "codex.cmd" })
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(无输出)";
        }

        var index = value.IndexOfAny(['\r', '\n']);
        return index < 0 ? value : value[..index];
    }
}
