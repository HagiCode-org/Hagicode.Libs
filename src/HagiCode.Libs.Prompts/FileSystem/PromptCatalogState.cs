using HagiCode.Libs.Prompts.Diagnostics;
using HagiCode.Libs.Prompts.Models;

namespace HagiCode.Libs.Prompts.FileSystem;

internal sealed class PromptCatalogState
{
    public PromptCatalogState(
        DateTimeOffset generatedAtUtc,
        IReadOnlyDictionary<string, PromptDefinition> effectiveDefinitions,
        IReadOnlyList<PromptCatalogIssue> issues)
    {
        GeneratedAtUtc = generatedAtUtc;
        EffectiveDefinitions = effectiveDefinitions;
        Issues = issues;
    }

    public DateTimeOffset GeneratedAtUtc { get; }

    public IReadOnlyDictionary<string, PromptDefinition> EffectiveDefinitions { get; }

    public IReadOnlyList<PromptCatalogIssue> Issues { get; }
}
