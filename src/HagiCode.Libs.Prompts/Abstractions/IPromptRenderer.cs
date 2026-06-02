using HagiCode.Libs.Prompts.Models;

namespace HagiCode.Libs.Prompts;

/// <summary>
/// Renders Handlebars prompt templates and validates their syntax.
/// </summary>
public interface IPromptRenderer
{
    /// <summary>
    /// Renders a prompt definition with the supplied parameters.
    /// </summary>
    string Render(PromptDefinition definition, IReadOnlyDictionary<string, object?>? parameters = null);

    /// <summary>
    /// Validates Handlebars syntax without rendering the template.
    /// </summary>
    PromptTemplateValidationResult ValidateSyntax(string templateContent, string? templatePath = null);

    /// <summary>
    /// Invalidates all compiled templates cached by the renderer.
    /// </summary>
    void InvalidateCache();
}
