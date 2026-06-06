namespace HagiCode.Libs.Providers.Reasonix;

using HagiCode.Libs.Providers;

/// <summary>
/// Describes a Reasonix ACP CLI invocation.
/// </summary>
public sealed record ReasonixOptions
{
    /// <summary>
    /// Gets or sets the custom Reasonix executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session and filesystem tools.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the Reasonix startup model override.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the Reasonix reasoning effort level.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// Gets or sets the optional session budget in USD.
    /// </summary>
    public decimal? BudgetUsd { get; init; }

    /// <summary>
    /// Gets or sets the optional transcript path written by Reasonix.
    /// </summary>
    public string? TranscriptPath { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether plan checkpoints should be auto-approved.
    /// </summary>
    public bool EnableYolo { get; init; }

    /// <summary>
    /// Gets or sets MCP server specifications forwarded to the CLI startup.
    /// </summary>
    public IReadOnlyList<string> McpServerSpecs { get; init; } = [];

    /// <summary>
    /// Gets or sets the MCP tool-name prefix.
    /// </summary>
    public string? McpPrefix { get; init; }

    /// <summary>
    /// Gets or sets the ACP bootstrap timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the Reasonix process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the ACP subcommand.
    /// Managed flags such as <c>--dir</c>, <c>--model</c>, and <c>--budget</c> are de-duplicated.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
