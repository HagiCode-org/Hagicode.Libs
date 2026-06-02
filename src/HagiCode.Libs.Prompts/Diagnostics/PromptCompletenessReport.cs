namespace HagiCode.Libs.Prompts.Diagnostics;

/// <summary>
/// Represents the completeness status for a required set of scenario-locale combinations.
/// </summary>
public sealed class PromptCompletenessReport
{
    /// <summary>
    /// Gets the total number of required combinations evaluated.
    /// </summary>
    public int TotalRequired { get; init; }

    /// <summary>
    /// Gets the number of required combinations that are present.
    /// </summary>
    public int Available { get; init; }

    /// <summary>
    /// Gets the list of missing combinations.
    /// </summary>
    public IReadOnlyList<PromptCompletenessGap> Missing { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the required combinations are complete.
    /// </summary>
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>
/// Describes a missing scenario-locale combination.
/// </summary>
public sealed class PromptCompletenessGap
{
    /// <summary>
    /// Gets the missing scenario identifier.
    /// </summary>
    public string Scenario { get; init; } = string.Empty;

    /// <summary>
    /// Gets the missing locale identifier.
    /// </summary>
    public string Locale { get; init; } = string.Empty;
}
