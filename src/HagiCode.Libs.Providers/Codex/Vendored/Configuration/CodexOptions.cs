using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Process;
using Microsoft.Extensions.Logging;

namespace ManagedCode.CodexSharpSDK.Configuration;

public sealed record CodexOptions
{
    public string? CodexExecutablePath { get; init; }

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public JsonObject? Config { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    public ILogger? Logger { get; init; }

    // HagiCode: the agent memory limit for the exec process; null runs it without a limit.
    internal ICliResourceLimitProvider? ResourceLimitProvider { get; init; }

    // HagiCode: the shared process ledger and the caller's session id that owns the Codex processes. Both are needed for
    // attribution only; the owner id is never sent to the CLI. Without either, the processes run unregistered.
    internal CliProcessManager? ProcessManager { get; init; }

    internal string? OwnerSessionId { get; init; }
}
