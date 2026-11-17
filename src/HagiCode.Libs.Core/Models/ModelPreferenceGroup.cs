namespace HagiCode.Libs.Core.Models;

/// <summary>
/// Preference groups a built-in model declares in its catalog entry.
/// Every model belongs to exactly one group; undeclared or unknown values are treated as <see cref="Other"/>.
/// </summary>
public static class ModelPreferenceGroup
{
    public const string Claude = "claude";
    public const string ClaudeCompatible = "claude-compatible";
    public const string Gpt = "gpt";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Claude, ClaudeCompatible, Gpt, Other];

    /// <summary>Returns the declared group, or <see cref="Other"/> when it is missing or not a known group.</summary>
    public static string Normalize(string? group)
    {
        var trimmed = group?.Trim();
        foreach (var known in All)
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return Other;
    }
}
