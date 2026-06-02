namespace HagiCode.Libs.Prompts.Models;

/// <summary>
/// Represents the outcome of validating Handlebars template syntax.
/// </summary>
public sealed class PromptTemplateValidationResult
{
    /// <summary>
    /// Gets a value indicating whether the template is syntactically valid.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Gets syntax validation errors.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static PromptTemplateValidationResult Success()
    {
        return new PromptTemplateValidationResult { IsValid = true };
    }

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static PromptTemplateValidationResult Failure(params string[] errors)
    {
        return new PromptTemplateValidationResult
        {
            IsValid = false,
            Errors = errors,
        };
    }
}
