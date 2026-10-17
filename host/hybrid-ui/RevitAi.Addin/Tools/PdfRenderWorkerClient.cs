using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace RevitAi.Addin.Tools;

internal static class PdfRenderWorkerClient
{
    private static readonly object Sync = new();

    // Only environment failures (missing worker or native runtime) are cached.
    // Per-document failures such as a bitmap allocation error must never be
    // cached: the previous implementation poisoned every later PDF call for
    // the rest of the Revit session.
    private static string? _cachedRuntimeFailure;

    public static async Task<JsonObject> RenderAsync(
        string filePath,
        int start,
        int end,
        int dpi,
        string? outputDirectory,
        CancellationToken cancellationToken,
        int grid = 0,
        string? region = null,
        bool fullTiles = false)
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(_cachedRuntimeFailure))
            {
                return new JsonObject
                {
                    ["success"] = false,
                    ["errorCode"] = "pdfium_unavailable",
                    ["errorMessage"] = _cachedRuntimeFailure
                };
            }
        }

        var workerDirectory =
            Environment.GetEnvironmentVariable("REVIT_AI_PDF_WORKER_DIR");
        if (string.IsNullOrWhiteSpace(workerDirectory))
        {
            var pluginDirectory = Path.GetDirectoryName(
                typeof(PdfRenderWorkerClient).Assembly.Location)
                ?? throw new InvalidOperationException("无法定位插件目录。");
            workerDirectory = Path.Combine(pluginDirectory, "PdfWorker");
        }
        var workerExecutable = Path.Combine(
            workerDirectory,
            "RevitAi.PdfWorker.exe");
        if (!File.Exists(workerExecutable))
        {
            return CacheRuntimeFailure(
                $"PDF Worker 不存在：{workerExecutable}");
        }

        var targetDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(
                Path.GetTempPath(),
                "RevitAi",
                "pdf-pages",
                Guid.NewGuid().ToString("N"))
            : outputDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = workerExecutable,
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(filePath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(targetDirectory);
        startInfo.ArgumentList.Add("--start");
        startInfo.ArgumentList.Add(start.ToString());
        startInfo.ArgumentList.Add("--end");
        startInfo.ArgumentList.Add(end.ToString());
        startInfo.ArgumentList.Add("--dpi");
        startInfo.ArgumentList.Add(dpi.ToString());
        if (grid > 0)
        {
            startInfo.ArgumentList.Add("--grid");
            startInfo.ArgumentList.Add(grid.ToString());
        }

        if (!string.IsNullOrWhiteSpace(region))
        {
            startInfo.ArgumentList.Add("--region");
            startInfo.ArgumentList.Add(region);
        }

        if (fullTiles)
        {
            startInfo.ArgumentList.Add("--full-tiles");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 PDF Worker。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(
            cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
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

            return Failure(
                "PDF Worker 执行超时（5 分钟）。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var jsonLine = stdout.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        JsonObject? response = null;
        if (!string.IsNullOrWhiteSpace(jsonLine))
        {
            try
            {
                response = JsonNode.Parse(jsonLine)?.AsObject();
            }
            catch
            {
            }
        }

        if (response?["success"]?.GetValue<bool>() == true)
        {
            response["outputDirectory"] = targetDirectory;
            return response;
        }

        var message = response?["error"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(message))
        {
            message = string.IsNullOrWhiteSpace(stderr)
                ? $"PDF Worker 退出码：{process.ExitCode}"
                : stderr.Trim();
        }

        if (message.Contains(
                "native runtime is incomplete",
                StringComparison.OrdinalIgnoreCase))
        {
            return CacheRuntimeFailure(message);
        }

        return Failure(message);
    }

    private static JsonObject CacheRuntimeFailure(string message)
    {
        lock (Sync)
        {
            _cachedRuntimeFailure = message;
        }

        return Failure(message);
    }

    private static JsonObject Failure(string message)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["errorCode"] = "exttools_tool_error",
            ["errorMessage"] = message
        };
    }
}
