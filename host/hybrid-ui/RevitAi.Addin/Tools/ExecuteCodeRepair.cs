using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitAi.Addin.Tools;

// The model regularly writes a bare value expression as its own statement,
// for example "g.Name;" or a multi-line member chain without an assignment.
// Roslyn rejects that with CS0201 ("Only assignment, call, increment,
// decrement, await, and new object expressions can be used as a statement")
// so the whole execute_code call fails. The compiler reports the exact line,
// so the statement can be rewritten as a discard assignment and retried.
internal static class ExecuteCodeRepair
{
    private static readonly Regex StatementError = new(
        @"\[第(?<line>\d+)行\s*Error\]\s*Only assignment, call, increment, "
        + @"decrement, await, and new object expressions can be used as a statement",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] StatementKeywords =
    [
        "return", "if", "else", "for", "foreach", "while", "do", "switch",
        "case", "default", "using", "var", "throw", "yield", "lock", "try",
        "catch", "finally", "new", "async", "await", "public", "private",
        "internal", "protected", "static", "void", "class", "struct", "goto",
        "break", "continue"
    ];

    public static bool TryRepair(
        string? error,
        JsonObject arguments,
        out string note)
    {
        note = string.Empty;
        if (string.IsNullOrWhiteSpace(error)
            || arguments["code"] is not JsonValue codeValue
            || !codeValue.TryGetValue<string>(out var code)
            || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var matches = StatementError.Matches(error);
        if (matches.Count == 0)
        {
            return false;
        }

        var lines = code
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var targets = new SortedSet<int>();
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups["line"].Value, out var lineNumber)
                && lineNumber >= 1
                && lineNumber <= lines.Length)
            {
                targets.Add(lineNumber - 1);
            }
        }

        var repaired = 0;
        foreach (var index in targets.Reverse())
        {
            var start = FindStatementStart(lines, index);
            if (start < 0 || !CanRewrite(lines[start]))
            {
                continue;
            }

            lines[start] = PrefixStatement(lines[start]);
            if (!lines[index].TrimEnd().EndsWith(';'))
            {
                lines[index] = lines[index].TrimEnd() + ";";
            }

            repaired++;
        }

        if (repaired == 0)
        {
            return false;
        }

        arguments["code"] = string.Join('\n', lines);
        note = $"execute_code 编译失败（CS0201），已自动把 {repaired} 处"
            + "裸表达式语句改写为赋值语句后重试。";
        return true;
    }

    private static int FindStatementStart(string[] lines, int index)
    {
        var start = index;
        while (start > 0 && IsContinuation(lines[start - 1]))
        {
            start--;
        }

        return start;
    }

    private static bool IsContinuation(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*')
            || trimmed.StartsWith('#'))
        {
            return false;
        }

        return trimmed[^1] is not (';' or '{' or '}' or ':');
    }

    private static bool CanRewrite(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith('#')
            || trimmed.StartsWith('{')
            || trimmed.StartsWith('}'))
        {
            return false;
        }

        foreach (var keyword in StatementKeywords)
        {
            if (trimmed.StartsWith(keyword, StringComparison.Ordinal)
                && (trimmed.Length == keyword.Length
                    || !char.IsLetterOrDigit(trimmed[keyword.Length])
                    && trimmed[keyword.Length] != '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string PrefixStatement(string line)
    {
        var indentation = line.Length - line.TrimStart().Length;
        return string.Concat(
            line.AsSpan(0, indentation),
            "_ = ",
            line.AsSpan(indentation));
    }
}
