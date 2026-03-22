namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Provides additive execution options for the shared CLI facade.
/// </summary>
public sealed record CliExecutionOptions
{
    /// <summary>
    /// Gets the requested execution mode.
    /// </summary>
    public CliExecutionMode Mode { get; init; } = CliExecutionMode.Buffered;

    /// <summary>
    /// Gets a display-friendly command preview override.
    /// </summary>
    public string? CommandPreview { get; init; }

    /// <summary>
    /// Gets a value indicating whether the runtime environment should be resolved before launch.
    /// </summary>
    public bool ResolveRuntimeEnvironment { get; init; } = true;
}
