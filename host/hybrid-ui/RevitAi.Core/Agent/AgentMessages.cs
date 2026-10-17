using System.Text.Json.Nodes;

namespace RevitAi.Core.Agent;

public sealed class AgentModelRequest
{
    public required IReadOnlyList<JsonObject> Messages { get; init; }

    public required IReadOnlyList<Tools.ToolDefinition> Tools { get; init; }

    public required string Model { get; init; }

    public IReadOnlyList<AgentAttachment> Attachments { get; init; } =
        Array.Empty<AgentAttachment>();
}

public sealed class AgentAttachment
{
    public required string AttachmentId { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public required string DataUrl { get; init; }

    public long Size { get; init; }

    public string? FilePath { get; set; }

    public string? ExtractedText { get; set; }

    public string? ExtractionError { get; set; }

    public bool IsImage =>
        ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed class AgentModelResponse
{
    public required JsonObject RawAssistantMessage { get; init; }

    public string? Content { get; init; }

    public IReadOnlyList<Tools.ToolCall> ToolCalls { get; init; } = Array.Empty<Tools.ToolCall>();
}

public interface IAgentModelClient
{
    Task<AgentModelResponse> CreateAsync(
        AgentModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AgentTurnResult
{
    public required string FinalText { get; init; }

    public required IReadOnlyList<Tools.ToolInvocationResult> ToolResults { get; init; }

    public required int Rounds { get; init; }
}

public sealed class AgentProgressEvent
{
    public required string Kind { get; init; }

    public int Round { get; init; }

    public string? Message { get; init; }

    public Tools.ToolCall? ToolCall { get; init; }

    public Tools.ToolInvocationResult? ToolResult { get; init; }

    public string? FinalText { get; init; }
}
