namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents one persisted managed CLI subprocess record.
/// </summary>
public sealed record CliOwnedProcessState(
    int Pid,
    DateTimeOffset StartedAtUtc,
    string ExecutablePath,
    string? WorkingDirectory,
    string ProviderName,
    DateTimeOffset CreatedAtUtc);
