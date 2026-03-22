using System.Text;

namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Describes a structured CLI execution request.
/// </summary>
public sealed record CliExecutionRequest
{
    /// <summary>
    /// Gets or sets the executable path or command name.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the argument tokens to pass to the executable.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets environment variable overrides applied after runtime environment resolution.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Gets or sets the output encoding for redirected streams.
    /// </summary>
    public Encoding OutputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Gets or sets the execution timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets or sets additive execution options.
    /// </summary>
    public CliExecutionOptions Options { get; init; } = new();
}
