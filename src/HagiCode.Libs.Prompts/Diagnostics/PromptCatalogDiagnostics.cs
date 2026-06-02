namespace HagiCode.Libs.Prompts.Diagnostics;

/// <summary>
/// Represents the latest prompt catalog diagnostics snapshot.
/// </summary>
public sealed class PromptCatalogDiagnostics
{
    /// <summary>
    /// Gets the UTC timestamp when the snapshot was generated.
    /// </summary>
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the number of effective prompt definitions published by the catalog.
    /// </summary>
    public int EffectivePromptCount { get; init; }

    /// <summary>
    /// Gets the number of effective prompt definitions sourced from the default directory.
    /// </summary>
    public int DefaultPromptCount { get; init; }

    /// <summary>
    /// Gets the number of effective prompt definitions sourced from the override directory.
    /// </summary>
    public int OverridePromptCount { get; init; }

    /// <summary>
    /// Gets the issues detected during the latest load.
    /// </summary>
    public IReadOnlyList<PromptCatalogIssue> Issues { get; init; } = [];
}
