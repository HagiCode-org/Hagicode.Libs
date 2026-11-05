namespace HagiCode.Libs.Providers.Dsh;

/// <summary>
/// Describes a DeepSeek Harness (dsh) ACP CLI invocation.
/// </summary>
public sealed record DshOptions
{
    /// <summary>
    /// Gets or sets the custom dsh executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session. dsh treats it as the workspace root.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the dsh provider id, for example <c>omniroute</c>. Required when <see cref="Model"/> is set.
    /// The provider and model are sent together as dsh's advertised <c>["provider","model"]</c> value through <c>session/set_config_option</c>.
    /// Ignored when <see cref="Model"/> is blank.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Gets or sets the model id exactly as dsh advertises it, for example <c>claude/claude-haiku-5-5</c>.
    /// It is forwarded without splitting, and boundary whitespace is trimmed.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to restore with <c>session/resume</c>.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether session reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets the ACP bootstrap timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables forwarded to the dsh process, for example <c>DSH_HOME</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the managed <c>--profile acp</c> bootstrap.
    /// Any <c>--profile</c> form is rejected because the ACP profile is always managed by the provider.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the in-memory chat images for this prompt. Each image is sent as an inline ACP image block when the
    /// connection advertises image input; otherwise it is listed by path in the prompt text. Bytes are never persisted or logged.
    /// </summary>
    public IReadOnlyList<DshImageAttachment> ImageAttachments { get; init; } = [];

    /// <summary>
    /// Gets or sets prompt manifest rows for chat images that the caller did not prepare (for example, images over the limit
    /// or missing files). They are always listed by path and carry no bytes.
    /// </summary>
    public IReadOnlyList<string> ImageManifestRows { get; init; } = [];
}
