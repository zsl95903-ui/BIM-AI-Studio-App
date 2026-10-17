using System.Text;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Agent;

internal static class ToolResultFormatter
{
    public static string Format(ToolInvocationResult result)
    {
        var builder = new StringBuilder();
        if (!result.Success)
        {
            builder.Append("❌ ")
                .Append(result.ToolName)
                .Append(" 执行失败: ")
                .Append(result.ErrorMessage ?? "未知错误");
            return builder.ToString();
        }

        builder.Append("✅ ")
            .Append(result.ToolName)
            .AppendLine(" 执行成功");

        if (result.Payload["message"] is System.Text.Json.Nodes.JsonValue messageValue
            && messageValue.TryGetValue<string>(out var message)
            && !string.IsNullOrWhiteSpace(message))
        {
            builder.AppendLine(message);
        }

        if (result.Payload["data"] is { } data)
        {
            builder.AppendLine("返回数据:");
            builder.AppendLine(data.ToJsonString());
        }

        return builder.ToString().TrimEnd();
    }
}
