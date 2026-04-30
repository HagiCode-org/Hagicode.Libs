using System.Text.Json.Nodes;
using ManagedCode.CodexSharpSDK.Client;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Describes a single SDK-backed Codex session request.
/// </summary>
public sealed record CodexSessionOptions
{
    /// <summary>
    /// Gets or sets the custom Codex executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the OpenAI API key override.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets or sets the OpenAI base URL override.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets or sets the thread id to resume.
    /// </summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// Gets or sets the optional raw Codex config override object.
    /// </summary>
    public JsonObject? Config { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the Codex process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets the per-thread execution options.
    /// </summary>
    public ThreadOptions ThreadOptions { get; init; } = new();
}
