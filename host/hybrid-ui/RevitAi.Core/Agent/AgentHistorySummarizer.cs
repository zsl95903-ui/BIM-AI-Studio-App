using System.Text;

namespace RevitAi.Core.Agent;

// Local heuristic summarizer based on message counts, the first user queries,
// the tools that were used and the time range.
internal static class AgentHistorySummarizer
{
    public const int KeepRecentCount = 50;
    public const int CompressionThreshold = 100;

    public static string SummarizeRange(
        IReadOnlyList<AgentSessionMessage> messages,
        int startIndex,
        int count)
    {
        if (messages.Count <= startIndex)
        {
            return string.Empty;
        }

        var slice = messages
            .Skip(startIndex)
            .Take(count)
            .Where(message => message.Role is "user" or "assistant")
            .ToList();
        if (slice.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"中间部分共有 {slice.Count} 条对话。");
        AppendDetails(builder, slice);
        return builder.ToString();
    }

    public static string Summarize(
        IReadOnlyList<AgentSessionMessage> messages,
        int keepCount)
    {
        if (messages.Count <= keepCount)
        {
            return string.Empty;
        }

        var slice = messages
            .Take(Math.Max(0, messages.Count - keepCount))
            .Where(message => message.Role is "user" or "assistant")
            .ToList();
        if (slice.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"本次会话共进行了 {slice.Count} 条对话。");
        AppendDetails(builder, slice);
        return builder.ToString();
    }

    private static void AppendDetails(
        StringBuilder builder,
        IReadOnlyList<AgentSessionMessage> slice)
    {
        var userQueries = slice
            .Where(message => message.Role == "user")
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrEmpty(content))
            .Select(content => content!)
            .ToList();
        var tools = slice
            .Where(message => message.ToolCalls is { Count: > 0 })
            .SelectMany(message => message.ToolCalls!.Select(call => call.ToolName))
            .Distinct()
            .ToList();

        if (userQueries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("**用户查询**：");
            foreach (var query in userQueries.Take(5))
            {
                var value = query.Length > 60 ? query[..60] + "..." : query;
                builder.AppendLine("- " + value);
            }

            if (userQueries.Count > 5)
            {
                builder.AppendLine($"- ... 还有 {userQueries.Count - 5} 条查询");
            }
        }

        if (tools.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("**使用的工具**：");
            foreach (var tool in tools)
            {
                builder.AppendLine("- " + tool);
            }
        }

        builder.AppendLine();
        builder.AppendLine(
            $"**时间范围**：{slice[0].Timestamp:HH:mm} - {slice[^1].Timestamp:HH:mm}");
    }
}
