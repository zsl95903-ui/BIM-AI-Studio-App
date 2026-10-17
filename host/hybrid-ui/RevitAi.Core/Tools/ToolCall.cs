using System.Text.Json.Nodes;

namespace RevitAi.Core.Tools;

public sealed class ToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required JsonObject Arguments { get; init; }
}

public sealed class ToolInvocationResult
{
    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    public bool Success { get; init; }

    public JsonObject Payload { get; init; } = new();

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public TimeSpan Duration { get; init; }
}

public interface IToolInvoker
{
    IReadOnlyList<ToolDefinition> Tools { get; }

    Task<ToolInvocationResult> InvokeAsync(
        ToolCall call,
        CancellationToken cancellationToken = default);
}
