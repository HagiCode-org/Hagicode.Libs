namespace HagiCode.Libs.Prompts.Models;

/// <summary>
/// Represents a file-backed prompt definition and its runtime readiness.
/// </summary>
public sealed class PromptDefinition
{
    /// <summary>
    /// Gets the prompt metadata.
    /// </summary>
    public PromptMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Gets the metadata file path.
    /// </summary>
    public string MetadataPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the Handlebars template file path.
    /// </summary>
    public string TemplatePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the raw template content.
    /// </summary>
    public string TemplateContent { get; init; } = string.Empty;

    /// <summary>
    /// Gets the template validation result captured during loading.
    /// </summary>
    public PromptTemplateValidationResult TemplateValidation { get; init; } = PromptTemplateValidationResult.Success();

    /// <summary>
    /// Gets a value indicating whether the prompt is ready for runtime use.
    /// </summary>
    public bool IsRuntimeReady { get; init; } = true;

    /// <summary>
    /// Gets the scenario identifier.
    /// </summary>
    public string Scenario => Metadata.Scenario;

    /// <summary>
    /// Gets the locale identifier.
    /// </summary>
    public string Locale => Metadata.Locale;

    /// <summary>
    /// Gets the effective source of the prompt definition.
    /// </summary>
    public PromptSource Source => Metadata.Source;
}
