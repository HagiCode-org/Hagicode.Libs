using HagiCode.Libs.Prompts.Models;

namespace HagiCode.Libs.Prompts;

/// <summary>
/// Provides access to the effective prompt catalog after file loading, override merging, and locale fallback resolution.
/// </summary>
public interface IPromptCatalog
{
    /// <summary>
    /// Gets the effective prompt definitions keyed by scenario-locale after override merging.
    /// </summary>
    IReadOnlyCollection<PromptDefinition> GetAllPrompts();

    /// <summary>
    /// Resolves a prompt for the given scenario and locale, falling back to the configured default locale when needed.
    /// </summary>
    PromptResolutionResult? Resolve(string scenario, string? locale = null);

    /// <summary>
    /// Reloads prompt files from disk and invalidates compiled template caches.
    /// </summary>
    void Reload();
}
