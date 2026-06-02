namespace HagiCode.Libs.Prompts.Models;

/// <summary>
/// Represents the outcome of resolving a prompt for a scenario and locale.
/// </summary>
public sealed class PromptResolutionResult
{
    /// <summary>
    /// Gets the effective prompt definition.
    /// </summary>
    public PromptDefinition Definition { get; init; } = new();

    /// <summary>
    /// Gets the locale requested by the caller.
    /// </summary>
    public string RequestedLocale { get; init; } = string.Empty;

    /// <summary>
    /// Gets the locale of the effective prompt definition.
    /// </summary>
    public string ResolvedLocale { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the catalog fell back to the default locale.
    /// </summary>
    public bool UsedFallback { get; init; }
}
