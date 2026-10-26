namespace HagiCode.Libs.Providers.Codebuddy;

using HagiCode.Libs.Providers;

/// <summary>
/// Describes a CodeBuddy ACP CLI invocation.
/// </summary>
public sealed record CodebuddyOptions
{
    /// <summary>
    /// Gets or sets the custom CodeBuddy executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the CodeBuddy model override.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to reuse.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or sets the ACP mode identifier applied after session bootstrap.
    /// </summary>
    public string? ModeId { get; init; }

    /// <summary>
    /// Gets a value indicating whether session reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets the ACP bootstrap timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the CodeBuddy process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the ACP bootstrap switch.
    /// Tokens are boundary-trimmed individually and whitespace-only tokens are ignored.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the per-execution steering channel. Leave it unset for a run that never steers.
    /// </summary>
    /// <remarks>
    /// Create a new <see cref="CodebuddyRunSteering" /> for each execution. Its outcomes arrive through
    /// <see cref="CodebuddyRunSteering.OutcomeObserved" /> while the run is iterated.
    /// </remarks>
    public CodebuddyRunSteering? Steering { get; init; }

    /// <summary>
    /// Gets or sets how long a steer may wait for CodeBuddy's acknowledgement. A steer without one in time is not steerable.
    /// It does not bound consumption. The default is 30 seconds. When a steering channel is supplied, a value that is not positive
    /// is rejected with an <see cref="ArgumentOutOfRangeException" /> before any process starts.
    /// </summary>
    public TimeSpan SteerDeliveryTimeout { get; init; } = DefaultSteerDeliveryTimeout;

    /// <summary>
    /// The default value of <see cref="SteerDeliveryTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultSteerDeliveryTimeout = TimeSpan.FromSeconds(30);
}
