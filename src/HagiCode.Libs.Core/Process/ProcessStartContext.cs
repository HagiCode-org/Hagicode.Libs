using System.Text;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Describes how to start a CLI subprocess.
/// </summary>
public sealed record ProcessStartContext
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Gets or sets the executable path or command name.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the argument list passed to the executable.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the working directory for the subprocess.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets environment variable overrides applied to the subprocess.
    /// A <see langword="null" /> value removes the variable from the child process.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Gets or sets the encoding used for redirected input streams.
    /// </summary>
    public Encoding InputEncoding { get; init; } = Utf8WithoutBom;

    /// <summary>
    /// Gets or sets the encoding used for redirected output streams.
    /// </summary>
    public Encoding OutputEncoding { get; init; } = Utf8WithoutBom;

    /// <summary>
    /// Gets or sets the maximum execution duration for one-shot commands.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets or sets the maximum number of characters retained per output stream during <see cref="CliProcessManager.ExecuteAsync" />.
    /// Output past the cap is still drained so the process can exit, but it is not kept.
    /// <see langword="null" /> keeps every character, which is the default.
    /// </summary>
    public int? MaxCapturedOutputCharacters { get; init; }

    /// <summary>
    /// Gets or sets provider ownership metadata for managed subprocess persistence.
    /// </summary>
    public CliProcessOwnershipRegistration? Ownership { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the active agent memory limit applies to this launch.
    /// Defaults to <see langword="true" />; version probes, discovery and other non-agent launches set it to <see langword="false" />.
    /// </summary>
    public bool ApplyResourceLimits { get; init; } = true;
}
