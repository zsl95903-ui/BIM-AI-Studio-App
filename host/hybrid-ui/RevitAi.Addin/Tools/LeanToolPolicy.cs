namespace RevitAi.Addin.Tools;

// Keeps the external tool registry intact while preventing optional cloud,
// authorization, and UI-dependent workflows from entering the lean runtime.
internal static class LeanToolPolicy
{
    private static readonly HashSet<string> BlockedTools = new(
        [
            "import_satellite_map",
            "load_family_from_library",
            "search_family_library",
            "web_search"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowed(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName)
        && !BlockedTools.Contains(toolName);

    public static bool IsBlocked(string? toolName) =>
        !IsAllowed(toolName);

    public static IReadOnlyCollection<string> Blocked => BlockedTools;
}
