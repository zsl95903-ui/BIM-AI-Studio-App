using System.Collections.Concurrent;
using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using RevitAi.Core.Tools;
using BimAi.Protocol;

namespace RevitAi.Addin.Services;

internal sealed class BridgePipeServer : IDisposable
{
    private readonly IToolInvoker _tools;
    private readonly AgentRuntimeService _agent;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly RequestJournal _journal = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly string _pipeName = $"RevitAi.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAi");
    private Task? _serverTask;
    private int _clientId;
    public BridgePipeServer(IToolInvoker tools, AgentRuntimeService agent) { _tools = tools; _agent = agent; }
    public void Start()
    {
        Directory.CreateDirectory(_directory);
        var discovery = new JsonObject { ["pipeName"] = _pipeName, ["hostId"] = _pipeName, ["protocolVersion"] = BridgeProtocol.Version, ["processId"] = Environment.ProcessId, ["startedAt"] = DateTimeOffset.UtcNow };
        foreach (var name in new[] { $"bridge.{Environment.ProcessId}.json", "bridge.json" })
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path + ".tmp", discovery.ToJsonString(), new UTF8Encoding(false));
            File.Move(path + ".tmp", path, true);
        }
        _serverTask = Task.Run(() => RunAsync(_shutdown.Token));
    }
    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var name in new[] { $"bridge.{Environment.ProcessId}.json", "bridge.json" })
        {
            var path = Path.Combine(_directory, name);
            try { if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["pipeName"]?.GetValue<string>() == _pipeName) File.Delete(path); } catch { }
        }
        // Do not block the Revit thread waiting for queued ExternalEvents.
    }
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new(_pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                var id = Interlocked.Increment(ref _clientId);
                var client = HandleClientAsync(pipe, token);
                pipe = null;
                _clients[id] = client;
                _ = client.ContinueWith(completed => { _clients.TryRemove(id, out _); }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch { if (!token.IsCancellationRequested) await Task.Delay(100, token); }
            finally { pipe?.Dispose(); }
        }
    }
    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            try
            {
                var line = await reader.ReadLineAsync(token);
                if (line is not null) await writer.WriteLineAsync((await HandleAsync(line, token)).ToJsonString());
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    private async Task<JsonObject> HandleAsync(string json, CancellationToken shutdown)
    {
        string? id = null;
        try
        {
            var request = JsonNode.Parse(json)!.AsObject();
            var type = request["type"]?.GetValue<string>() ?? "invoke";
            id = request["requestId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            if (type == "request.cancel") return _journal.Cancel(id);
            if (type == "request.status") return _journal.Status(id);
            if (request["hostId"] is JsonValue host && host.GetValue<string>() != _pipeName) return BridgeProtocol.Error(id, "host_mismatch", "The target host changed.");
            if (request["protocolVersion"] is JsonValue version && version.GetValue<string>() != BridgeProtocol.Version) return BridgeProtocol.Error(id, "protocol_mismatch", "Unsupported protocol version.");
            if (type == "agent.status") return new() { ["success"] = true, ["status"] = _agent.GetStatus(), ["hostId"] = _pipeName, ["protocolVersion"] = BridgeProtocol.Version };
            if (type == "tools.list") return new() { ["success"] = true, ["tools"] = _agent.GetStatus()["tools"]!.DeepClone(), ["protocolVersion"] = BridgeProtocol.Version };
            if (type == "agent.test") return new() { ["success"] = true, ["result"] = await _agent.TestConnectionAsync(shutdown) };
            if (type is not ("invoke" or "agent.chat")) return BridgeProtocol.Error(id, "invalid_request", "Unsupported request type.");
            var requestId = id;
            return await _journal.ExecuteAsync(id, json, async token =>
            {
                if (type == "agent.chat")
                {
                    var chat = await _agent.RunAsync(request["message"]!.GetValue<string>(), token);
                    return new() { ["success"] = true, ["requestId"] = requestId, ["finalText"] = chat.FinalText, ["state"] = "completed" };
                }
                var name = request["tool"]!.GetValue<string>();
                var definition = _tools.Tools.FirstOrDefault(t => t.Name == name);
                var expected = request["expectedDocumentGuid"]?.GetValue<string>();
                if (definition?.Mutating == true && string.IsNullOrWhiteSpace(expected)) return BridgeProtocol.Error(requestId, "document_required", "Bind mutations to expectedDocumentGuid.");
                var result = await _tools.InvokeAsync(new() { Id = requestId, Name = name, Arguments = request["arguments"]?.DeepClone().AsObject() ?? new(), ExpectedDocumentGuid = expected }, token);
                return new() { ["type"] = "result", ["requestId"] = requestId, ["tool"] = name, ["success"] = result.Success, ["payload"] = result.Payload.DeepClone(), ["errorCode"] = result.ErrorCode, ["errorMessage"] = result.ErrorMessage, ["state"] = result.Success ? "completed" : "failed", ["protocolVersion"] = BridgeProtocol.Version, ["hostId"] = _pipeName };
            }, shutdown);
        }
        catch (Exception ex) { return BridgeProtocol.Error(id, "bridge_error", ex.GetBaseException().Message); }
    }
}
