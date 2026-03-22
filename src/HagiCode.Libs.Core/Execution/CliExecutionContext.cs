using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Represents the fully-resolved launch context for one CLI execution.
/// </summary>
public sealed record CliExecutionContext
{
    private CliExecutionContext()
    {
    }

    /// <summary>
    /// Gets the executable path or command name.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// Gets the structured argument tokens.
    /// </summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Gets the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the merged environment variables.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; }

    /// <summary>
    /// Gets the output encoding.
    /// </summary>
    public required System.Text.Encoding OutputEncoding { get; init; }

    /// <summary>
    /// Gets the timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets the requested execution mode.
    /// </summary>
    public required CliExecutionMode Mode { get; init; }

    /// <summary>
    /// Gets the display-friendly command preview.
    /// </summary>
    public required string CommandPreview { get; init; }

    /// <summary>
    /// Builds a resolved execution context from a request and runtime environment.
    /// </summary>
    public static CliExecutionContext Create(
        CliExecutionRequest request,
        IReadOnlyDictionary<string, string?>? runtimeEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mergedEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (runtimeEnvironment is not null)
        {
            foreach (var entry in runtimeEnvironment)
            {
                mergedEnvironment[entry.Key] = entry.Value;
            }
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (var entry in request.EnvironmentVariables)
            {
                mergedEnvironment[entry.Key] = entry.Value;
            }
        }

        var arguments = request.Arguments
            .Where(static argument => argument is not null)
            .ToArray();

        return new CliExecutionContext
        {
            ExecutablePath = request.ExecutablePath,
            Arguments = arguments,
            WorkingDirectory = request.WorkingDirectory,
            EnvironmentVariables = mergedEnvironment,
            OutputEncoding = request.OutputEncoding,
            Timeout = request.Timeout,
            Mode = request.Options.Mode,
            CommandPreview = string.IsNullOrWhiteSpace(request.Options.CommandPreview)
                ? CommandPreviewFormatter.Format(request.ExecutablePath, arguments)
                : request.Options.CommandPreview!
        };
    }

    /// <summary>
    /// Converts the execution context into a process start context.
    /// </summary>
    public ProcessStartContext ToProcessStartContext()
    {
        return new ProcessStartContext
        {
            ExecutablePath = ExecutablePath,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            EnvironmentVariables = EnvironmentVariables,
            OutputEncoding = OutputEncoding,
            Timeout = Timeout
        };
    }
}
