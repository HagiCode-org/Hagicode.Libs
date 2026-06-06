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
    /// Gets or sets the Reasonix 1.x ACP bootstrap model selector.
    /// This is forwarded as <c>reasonix acp -model &lt;value&gt;</c>.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to reuse.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether session reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets the legacy Reasonix reasoning effort level.
    /// Reasonix 1.x ACP no longer accepts an effort bootstrap flag, so this value is currently ignored.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// Gets or sets the legacy optional session budget in USD.
    /// Reasonix 1.x ACP no longer accepts a budget bootstrap flag, so this value is currently ignored.
    /// </summary>
    public decimal? BudgetUsd { get; init; }

    /// <summary>
    /// Gets or sets the legacy transcript output path.
    /// Reasonix 1.x ACP no longer accepts a transcript bootstrap flag, so this value is currently ignored.
    /// </summary>
    public string? TranscriptPath { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether plan checkpoints should be auto-approved.
    /// Reasonix 1.x ACP no longer exposes a startup-time yolo flag, so this value is currently ignored.
    /// </summary>
    public bool EnableYolo { get; init; }

    /// <summary>
    /// Gets or sets legacy MCP server specifications that used to be forwarded to the CLI startup.
    /// Reasonix 1.x ACP no longer accepts MCP bootstrap flags; configure plugins in <c>reasonix.toml</c> instead.
    /// </summary>
    public IReadOnlyList<string> McpServerSpecs { get; init; } = [];

    /// <summary>
    /// Gets or sets the legacy MCP tool-name prefix.
    /// Reasonix 1.x ACP no longer accepts this bootstrap flag, so this value is currently ignored.
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
    /// Known legacy 0.x bootstrap flags are stripped so the invocation remains compatible with Reasonix 1.x ACP.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
