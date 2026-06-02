namespace HagiCode.Libs.Prompts.Configuration;

/// <summary>
/// Configures how the file-backed prompt catalog loads prompt metadata and templates.
/// </summary>
public sealed class PromptManagementOptions
{
    /// <summary>
    /// Default configuration section name for hosts using app configuration binding.
    /// </summary>
    public const string SectionName = "Prompts";

    /// <summary>
    /// Gets or sets the primary prompt directory.
    /// Relative paths are resolved from <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public string RootPath { get; set; } = "Resources/Prompts";

    /// <summary>
    /// Gets or sets the override prompt directory.
    /// When omitted, the library uses a sibling <c>OverridePrompts</c> directory next to <see cref="RootPath"/>.
    /// </summary>
    public string? OverridePath { get; set; }

    /// <summary>
    /// Gets or sets the default locale used for fallback resolution.
    /// </summary>
    public string DefaultLocale { get; set; } = "en-US";

    /// <summary>
    /// Gets or sets a value indicating whether a missing root directory should fail fast.
    /// </summary>
    public bool StrictMode { get; set; } = true;

    /// <summary>
    /// Gets or sets how invalid Handlebars syntax is handled during catalog loading.
    /// </summary>
    public PromptTemplateValidationMode TemplateValidationMode { get; set; } = PromptTemplateValidationMode.Strict;

    /// <summary>
    /// Gets or sets the maximum number of compiled templates retained by the renderer.
    /// </summary>
    public int TemplateCacheSize { get; set; } = 100;
}

/// <summary>
/// Controls how invalid Handlebars templates are handled during catalog loading.
/// </summary>
public enum PromptTemplateValidationMode
{
    /// <summary>
    /// Records template validation diagnostics but still publishes the prompt definition.
    /// </summary>
    Lenient,

    /// <summary>
    /// Refuses to publish invalid templates for runtime use.
    /// </summary>
    Strict,
}
