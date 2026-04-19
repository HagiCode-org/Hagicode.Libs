namespace HagiCode.Libs.Providers.DeepAgents;

using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers;

/// <summary>
/// Describes a DeepAgents ACP CLI invocation.
/// </summary>
public sealed record DeepAgentsOptions
{
    /// <summary>
    /// Gets or sets the custom DeepAgents executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// When <see cref="WorkspaceRoot" /> is supplied it takes precedence.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the explicit DeepAgents workspace root.
    /// </summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>
    /// Gets or sets the agent name forwarded to the DeepAgents launcher.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Gets or sets the agent description forwarded to the DeepAgents launcher.
    /// </summary>
    public string? AgentDescription { get; init; }

    /// <summary>
    /// Gets or sets the optional model override forwarded during managed startup.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the skills directories forwarded to the launcher.
    /// </summary>
    public IReadOnlyList<string> SkillsDirectories { get; init; } = [];

    /// <summary>
    /// Gets or sets AGENTS-style memory files forwarded to the launcher.
    /// </summary>
    public IReadOnlyList<string> MemoryFiles { get; init; } = [];

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
    /// Gets or sets environment variables injected into the DeepAgents process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the managed launcher arguments.
    /// Managed bootstrap arguments are normalized from typed properties instead of this collection.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets provider-level pooling overrides.
    /// </summary>
    public CliPoolSettings? PoolSettings { get; init; }
}
