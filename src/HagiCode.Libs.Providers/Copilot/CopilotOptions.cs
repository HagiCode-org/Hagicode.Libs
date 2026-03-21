namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// Describes a GitHub Copilot CLI invocation routed through the SDK-managed session path.
/// </summary>
public sealed record CopilotOptions
{
    /// <summary>
    /// Default request timeout for Copilot prompt execution.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    /// <summary>
    /// Default startup timeout used while booting the Copilot SDK session.
    /// </summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the custom Copilot executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the Copilot model override.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the working directory used for SDK session creation.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the request timeout used for one Copilot prompt.
    /// </summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Gets or sets the startup timeout used while creating the SDK session.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = DefaultStartupTimeout;

    /// <summary>
    /// Gets or sets the Copilot authentication source.
    /// </summary>
    public CopilotAuthSource AuthSource { get; init; } = CopilotAuthSource.LoggedInUser;

    /// <summary>
    /// Gets or sets the GitHub token used when <see cref="AuthSource" /> is <see cref="CopilotAuthSource.GitHubToken" />.
    /// </summary>
    public string? GitHubToken { get; init; }

    /// <summary>
    /// Gets or sets the optional Copilot CLI URL override.
    /// </summary>
    public string? CliUrl { get; init; }

    /// <summary>
    /// Gets or sets Copilot permission defaults forwarded to verified CLI startup arguments.
    /// </summary>
    public CopilotPermissionOptions Permissions { get; init; } = new();

    /// <summary>
    /// Gets or sets environment variables applied while launching the SDK-managed Copilot runtime.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional Copilot startup arguments that are filtered through the verified compatibility matrix.
    /// </summary>
    public IReadOnlyList<string> AdditionalArgs { get; init; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether to disable ask-user prompts during execution.
    /// </summary>
    public bool NoAskUser { get; init; } = true;
}

/// <summary>
/// Defines the Copilot authentication source.
/// </summary>
public enum CopilotAuthSource
{
    /// <summary>
    /// Use the locally logged-in Copilot account.
    /// </summary>
    LoggedInUser = 0,

    /// <summary>
    /// Use an explicit GitHub token supplied in <see cref="CopilotOptions.GitHubToken" />.
    /// </summary>
    GitHubToken = 1
}

/// <summary>
/// Permission settings forwarded to compatible Copilot CLI startup flags.
/// </summary>
public sealed record CopilotPermissionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether all tools are allowed.
    /// </summary>
    public bool AllowAllTools { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether all filesystem paths are allowed.
    /// </summary>
    public bool AllowAllPaths { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether all URLs are allowed.
    /// </summary>
    public bool AllowAllUrls { get; init; }

    /// <summary>
    /// Gets or sets the explicitly allowed tool names.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicitly allowed filesystem paths.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicitly denied tool names.
    /// </summary>
    public IReadOnlyList<string> DeniedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicitly denied URLs.
    /// </summary>
    public IReadOnlyList<string> DeniedUrls { get; init; } = [];
}
