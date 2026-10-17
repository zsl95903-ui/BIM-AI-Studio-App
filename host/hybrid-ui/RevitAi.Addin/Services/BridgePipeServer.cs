using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Services;

internal sealed class BridgePipeServer : IDisposable
{
    private readonly IToolInvoker _tools;
    private readonly AgentRuntimeService _agent;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private readonly string _discoveryPath;
    private Task? _serverTask;

    public BridgePipeServer(IToolInvoker tools, AgentRuntimeService agent)
    {
        _tools = tools;
        _agent = agent;
        _pipeName = $"RevitAi.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _discoveryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitAi",
            "bridge.json");
    }

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_discoveryPath)!);
        File.WriteAllText(
            _discoveryPath,
            new JsonObject
            {
                ["pipeName"] = _pipeName,
                ["processId"] = Environment.ProcessId,
                ["startedAt"] = DateTimeOffset.UtcNow
            }.ToJsonString(),
            Encoding.UTF8);

        _serverTask = Task.Run(() => RunAsync(_shutdown.Token));
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The process is shutting down.
        }

        _shutdown.Dispose();

        try
        {
            if (File.Exists(_discoveryPath))
            {
                File.Delete(_discoveryPath);
            }
        }
        catch
        {
            // Discovery cleanup is best-effort.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    leaveOpen: true);
                await using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true)
                {
                    AutoFlush = true
                };

                while (pipe.IsConnected
                       && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    var response = await HandleRequestAsync(
                        line,
                        cancellationToken);
                    await writer.WriteLineAsync(response.ToJsonString());
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A disconnected client must not stop the bridge listener.
                try
                {
                    await Task.Delay(250, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<JsonObject> HandleRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonNode.Parse(requestJson)?.AsObject()
                ?? throw new InvalidOperationException("Invalid JSON request.");

            var type = request["type"]?.GetValue<string>();

            if (type == "agent.status")
            {
                return new JsonObject
                {
                    ["type"] = "agent.status",
                    ["success"] = true,
                    ["status"] = _agent.GetStatus()
                };
            }

            if (type == "agent.test")
            {
                var agentResult = await _agent.TestConnectionAsync(cancellationToken);
                return new JsonObject
                {
                    ["type"] = "agent.test",
                    ["success"] = true,
                    ["result"] = agentResult
                };
            }

            if (type == "agent.chat")
            {
                var message = request["message"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Missing 'message'.");
                var chatResult = await _agent.RunAsync(message, cancellationToken);
                return new JsonObject
                {
                    ["type"] = "agent.chat",
                    ["success"] = true,
                    ["finalText"] = chatResult.FinalText,
                    ["rounds"] = chatResult.Rounds,
                    ["toolResults"] = new JsonArray(
                        chatResult.ToolResults.Select(item => new JsonObject
                        {
                            ["toolCallId"] = item.ToolCallId,
                            ["toolName"] = item.ToolName,
                            ["success"] = item.Success,
                            ["payload"] = item.Payload.DeepClone(),
                            ["errorCode"] = item.ErrorCode,
                            ["errorMessage"] = item.ErrorMessage
                        }).Cast<JsonNode?>().ToArray())
                };
            }

            if (type != "invoke")
            {
                return Error(
                    null,
                    "invalid_request",
                    "Supported types: invoke, agent.status, agent.test, agent.chat.");
            }

            var toolName = request["tool"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Missing 'tool'.");
            var arguments = request["arguments"]?.AsObject() ?? new JsonObject();
            var call = new ToolCall
            {
                Id = request["requestId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                Name = toolName,
                Arguments = arguments
            };

            var result = await _tools.InvokeAsync(call, cancellationToken);
            return new JsonObject
            {
                ["type"] = "result",
                ["requestId"] = call.Id,
                ["success"] = result.Success,
                ["tool"] = result.ToolName,
                ["durationMs"] = result.Duration.TotalMilliseconds,
                ["payload"] = result.Payload.DeepClone(),
                ["errorCode"] = result.ErrorCode,
                ["errorMessage"] = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            return Error(null, "bridge_error", ex.GetBaseException().Message);
        }
    }

    private static JsonObject Error(string? requestId, string code, string message)
    {
        return new JsonObject
        {
            ["type"] = "error",
            ["requestId"] = requestId,
            ["success"] = false,
            ["errorCode"] = code,
            ["errorMessage"] = message
        };
    }
}
