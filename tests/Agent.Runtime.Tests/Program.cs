using System.Text.Json.Nodes;
using RevitAi.Core.Agent;
using RevitAi.Core.Tools;

using var cancelled = new CancellationTokenSource();
var invoker = new FakeTools();
var model = new FakeModel();
using var loop = new AgentLoop(model, invoker, "fake");
var session = new AgentSessionState();
try
{
    await loop.RunAsync(session, "create two levels", cancelled.Token, progress =>
    {
        if (progress.Kind == "tool_completed") cancelled.Cancel();
        return Task.CompletedTask;
    });
    throw new Exception("Expected cancellation");
}
catch (OperationCanceledException) { }
if (invoker.Writes != 1) throw new Exception("Cancellation allowed another mutation");
if (!session.Messages.Any(m => m.Role == "tool" && m.ToolCallId == "one")) throw new Exception("Committed result lost on cancellation");
if (session.Messages.Any(m => m.Role == "tool" && m.ToolCallId == "two")) throw new Exception("Unexecuted operation was recorded");
var capture = new CaptureModel();
using (var nextLoop = new AgentLoop(capture, invoker, "fake")) await nextLoop.RunAsync(session, "what was created?");
if (!capture.History.Contains("createdId") || !capture.History.Contains("42")) throw new Exception("Root payload lost across turns");
if (!capture.History.Contains("success")) throw new Exception("Tool outcome lost across turns");
Console.WriteLine("Agent: complete tool result JSON and cross-turn evidence retention passed.");
Console.WriteLine("Agent: run document binding, cancellation boundary, and partial result persistence passed.");

sealed class FakeTools : IToolInvoker
{
    public int Writes;
    public IReadOnlyList<ToolDefinition> Tools => [new() { Name = "get_document_snapshot", Description = "snapshot", InputSchema = new() }];
    public Task<ToolInvocationResult> InvokeAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        if (call.Name == "get_document_snapshot") return Task.FromResult(new ToolInvocationResult { ToolCallId = call.Id, ToolName = call.Name, Success = true, Payload = new() { ["documentGuid"] = "fixed-document" } });
        if (call.ExpectedDocumentGuid != "fixed-document") throw new Exception("Target changed between tools");
        Writes++;
        return Task.FromResult(new ToolInvocationResult { ToolCallId = call.Id, ToolName = call.Name, Success = true, Payload = new() { ["createdId"] = 42 } });
    }
}
sealed class FakeModel : IAgentModelClient
{
    public Task<AgentModelResponse> CreateAsync(AgentModelRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AgentModelResponse
    {
        RawAssistantMessage = new(), ToolCalls = [new() { Id = "one", Name = "create_level", Arguments = new() }, new() { Id = "two", Name = "create_level", Arguments = new() }]
    });
}

sealed class CaptureModel : IAgentModelClient
{
    public string History = "";
    public Task<AgentModelResponse> CreateAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
    {
        History = string.Join("\n", request.Messages.Select(m => m.ToJsonString()));
        return Task.FromResult(new AgentModelResponse { RawAssistantMessage = new(), Content = "Created 42" });
    }
}
