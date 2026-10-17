using System.Text;
using System.Text.Json.Nodes;
using RevitAi.Abstractions.AI;
using RevitAi.Core.AI;

namespace RevitAi.Addin.Tools;

// The previous UI never exposed the skill executor as a callable tool: the
// skill list was only injected as chat text. This wrapper registers the
// original SkillManager.ExecuteSkillAsync as a normal IAITool named
// "execute_skill", using the schema the previous SkillManager generates, so
// the model can actually run a skill.
internal sealed class SkillExecutionTool : IAITool
{
    private readonly ISkillManager _skillManager;

    public SkillExecutionTool(ISkillManager skillManager)
    {
        _skillManager = skillManager;
    }

    public string Name => "execute_skill";

    public string Category => "技能";

    public string Description =>
        "执行预定义的工作流程（技能），包含多个工具调用步骤。"
        + $"可用技能：{string.Join("、", DescribeSkills())}";

    public string ParametersSchema => BuildSchema();

    public async Task<AIToolResult> ExecuteAsync(
        AIToolContext context,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        var skillName = context.GetParameter("skillName", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return AIToolResult.Fail("缺少参数 skillName。");
        }

        var variables = ReadVariables(context);
        try
        {
            var result = await _skillManager.ExecuteSkillAsync(
                skillName,
                variables,
                context,
                cancellationToken);
            if (!result.IsSuccess)
            {
                return AIToolResult.Fail(result.Error ?? "技能执行失败。");
            }

            var steps = result.Value ?? [];
            var builder = new StringBuilder();
            builder.Append("技能 ").Append(skillName).Append(" 执行完成，共 ")
                .Append(steps.Count).Append(" 步：").AppendLine();
            foreach (var step in steps)
            {
                builder.Append("- ")
                    .Append(step.Success ? "成功" : "失败")
                    .Append(": ")
                    .AppendLine(
                        string.IsNullOrWhiteSpace(step.Message)
                            ? (step.Error ?? string.Empty)
                            : step.Message);
            }

            return AIToolResult.Ok(
                builder.ToString().TrimEnd(),
                new
                {
                    skill = skillName,
                    step_count = steps.Count,
                    success_count = steps.Count(step => step.Success)
                });
        }
        catch (Exception ex)
        {
            return AIToolResult.Fail($"技能执行异常: {ex.GetBaseException().Message}");
        }
    }

    private IEnumerable<string> DescribeSkills()
    {
        var list = _skillManager.GetSkillList();
        if (!list.IsSuccess || list.Value is null)
        {
            return Array.Empty<string>();
        }

        return list.Value.Select(skill =>
            $"{skill.SkillName}({skill.DisplayName})");
    }

    private string BuildSchema()
    {
        var names = new JsonArray();
        var list = _skillManager.GetSkillList();
        if (list.IsSuccess && list.Value is not null)
        {
            foreach (var skill in list.Value)
            {
                names.Add(skill.SkillName);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["skillName"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "技能名称",
                    ["enum"] = names
                },
                ["variables"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] =
                        "技能参数（变量名和值的键值对，例如 {\"levelId\": \"123\"}）"
                }
            },
            ["required"] = new JsonArray("skillName")
        };
        return schema.ToJsonString();
    }

    private static Dictionary<string, object> ReadVariables(AIToolContext context)
    {
        var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!context.Parameters.TryGetValue("variables", out var raw) || raw is null)
        {
            return variables;
        }

        switch (raw)
        {
            case IDictionary<string, object> dictionary:
                foreach (var pair in dictionary)
                {
                    variables[pair.Key] = pair.Value;
                }

                break;

            case string text when !string.IsNullOrWhiteSpace(text):
                try
                {
                    if (JsonNode.Parse(text) is JsonObject jsonObject)
                    {
                        foreach (var pair in jsonObject)
                        {
                            variables[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                        }
                    }
                }
                catch
                {
                    // Ignore malformed variables; the skill reports missing ones.
                }

                break;
        }

        return variables;
    }
}

