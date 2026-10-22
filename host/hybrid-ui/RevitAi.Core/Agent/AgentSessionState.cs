namespace RevitAi.Core.Agent;

// The session keeps the full message list, including tool messages, plus the
// accumulated summary used to compress long conversations.
public sealed class AgentSessionMessage
{
    public required string Role { get; init; }

    public string? Content { get; set; }

    public string? ToolCallId { get; init; }

    public List<AgentToolCallRecord>? ToolCalls { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.Now;
}

public sealed class AgentToolCallRecord
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public string? ArgumentsJson { get; init; }
}

public sealed class AgentSessionState
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "新对话";

    public List<AgentSessionMessage> Messages { get; } = [];

    public int RoundCount { get; set; }

    public string? AccumulatedSummary { get; set; }

    public int SummaryMessageCount { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public void Clear()
    {
        Messages.Clear();
        AccumulatedSummary = null;
        SummaryMessageCount = 0;
        RoundCount = 0;
        UpdatedAt = DateTime.Now;
    }
}
