using System.Text.Json.Nodes;
using RevitAi.Core.Tools;

namespace RevitAi.Core.Agent;

internal static class ToolResultFormatter
{
    public static string Format(ToolInvocationResult result) => new JsonObject
    {
        ["toolCallId"] = result.ToolCallId,
        ["toolName"] = result.ToolName,
        ["success"] = result.Success,
        ["durationMs"] = result.Duration.TotalMilliseconds,
        ["payload"] = result.Payload.DeepClone(),
        ["errorCode"] = result.ErrorCode,
        ["errorMessage"] = result.ErrorMessage
    }.ToJsonString();
}
