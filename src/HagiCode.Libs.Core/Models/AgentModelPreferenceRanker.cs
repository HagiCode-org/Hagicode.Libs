namespace HagiCode.Libs.Core.Models;

/// <summary>A model to rank: its catalog identity, catalog order and declared preference group.</summary>
public sealed record AgentModelPreferenceCandidate(
    string Id,
    string Name,
    int SortOrder,
    string? PreferenceGroup);

/// <summary>
/// Orders models for one agent CLI family using the family's preference profile.
/// Families without a profile keep catalog order. Ties are broken by sortOrder, name, then id (ordinal).
/// </summary>
public static class AgentModelPreferenceRanker
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Codex: GPT first, everything else in catalog order.
            ["codex"] = [ModelPreferenceGroup.Gpt],
            // Claude: Claude, then Claude-compatible, then GPT, then everything else.
            ["claude"] =
            [
                ModelPreferenceGroup.Claude,
                ModelPreferenceGroup.ClaudeCompatible,
                ModelPreferenceGroup.Gpt,
            ],
        };

    /// <summary>
    /// Returns the models in preference order for <paramref name="agentCliFamily"/>.
    /// The zero-based index of each returned model is its preference rank for that family.
    /// </summary>
    public static IReadOnlyList<AgentModelPreferenceCandidate> Order(
        string agentCliFamily,
        IEnumerable<AgentModelPreferenceCandidate> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var list = models.ToList();

        var profile = ResolveProfile(agentCliFamily);
        if (profile is null)
        {
            return list
                .OrderBy(model => model.SortOrder)
                .ThenBy(model => model.Name, StringComparer.Ordinal)
                .ThenBy(model => model.Id, StringComparer.Ordinal)
                .ToList();
        }

        return list
            .OrderBy(model => TierOf(profile, ModelPreferenceGroup.Normalize(model.PreferenceGroup)))
            .ThenBy(model => model.SortOrder)
            .ThenBy(model => model.Name, StringComparer.Ordinal)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Returns true when the agent CLI family has a preference profile.</summary>
    public static bool HasProfile(string agentCliFamily) => ResolveProfile(agentCliFamily) is not null;

    private static IReadOnlyList<string>? ResolveProfile(string agentCliFamily)
    {
        if (string.IsNullOrWhiteSpace(agentCliFamily))
        {
            return null;
        }

        return Profiles.TryGetValue(agentCliFamily.Trim(), out var profile) ? profile : null;
    }

    private static int TierOf(IReadOnlyList<string> profile, string group)
    {
        var index = profile.ToList().IndexOf(group);
        // Groups not named by the profile (including "other") share the last tier.
        return index >= 0 ? index : profile.Count;
    }
}
