using System.Text.Json.Nodes;

namespace RevitAi.Core.Tools;

public sealed class ToolDefinition
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required JsonObject InputSchema { get; init; }

    public bool Mutating { get; init; }

    public bool RequiresTransaction { get; init; }

    public bool SupportsDryRun { get; init; }

    public string ToolSet { get; init; } = "query";

    public JsonObject ToOpenAiTool()
    {
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["parameters"] = InputSchema.DeepClone()
            }
        };
    }
}
