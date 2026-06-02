using HagiCode.Libs.Prompts.Models;

namespace HagiCode.Libs.Prompts.Diagnostics;

/// <summary>
/// Represents a catalog loading or validation issue.
/// </summary>
public sealed class PromptCatalogIssue
{
    /// <summary>
    /// Gets the issue kind.
    /// </summary>
    public PromptCatalogIssueKind Kind { get; init; }

    /// <summary>
    /// Gets the source directory the issue originated from.
    /// </summary>
    public PromptSource? Source { get; init; }

    /// <summary>
    /// Gets the file path associated with the issue.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the scenario associated with the issue when known.
    /// </summary>
    public string? Scenario { get; init; }

    /// <summary>
    /// Gets the locale associated with the issue when known.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Gets the human-readable issue message.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Classifies prompt catalog issues.
/// </summary>
public enum PromptCatalogIssueKind
{
    RootDirectoryMissing,
    MissingMetadata,
    MissingTemplate,
    InvalidMetadata,
    InvalidTemplateSyntax,
}
