using HagiCode.Libs.Prompts.Diagnostics;

namespace HagiCode.Libs.Prompts;

/// <summary>
/// Exposes prompt catalog diagnostics without requiring hosts to read prompt files directly.
/// </summary>
public interface IPromptDiagnosticsService
{
    /// <summary>
    /// Gets the latest catalog diagnostics snapshot.
    /// </summary>
    PromptCatalogDiagnostics GetSnapshot();

    /// <summary>
    /// Validates that the effective catalog covers every required scenario-locale combination.
    /// </summary>
    PromptCompletenessReport ValidateCompleteness(IEnumerable<string> requiredScenarios, IEnumerable<string> requiredLocales);
}
