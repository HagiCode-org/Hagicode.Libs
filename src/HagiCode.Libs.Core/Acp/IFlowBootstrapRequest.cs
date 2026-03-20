namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes how to resolve or start an IFlow ACP endpoint.
/// </summary>
public sealed record IFlowBootstrapRequest
{
    /// <summary>
    /// Gets or sets the explicit ACP endpoint to connect to.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Gets or sets the resolved executable path used for managed bootstrap.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory for the managed CLI process.
    /// </summary>
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets or sets environment variable overrides applied to the managed CLI process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional CLI arguments appended after the ACP bootstrap switches.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the maximum time allowed for the managed endpoint to become ready.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
