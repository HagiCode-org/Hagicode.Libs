namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents one recent pool eviction or fault event.
/// </summary>
/// <param name="ProviderName">The provider that owned the entry.</param>
/// <param name="SessionId">The session identifier that was evicted, when known.</param>
/// <param name="Reason">The reason that caused the event.</param>
/// <param name="OccurredAtUtc">The event timestamp in UTC.</param>
public sealed record CliAcpSessionPoolRecentEventDiagnostics(
    string ProviderName,
    string? SessionId,
    CliAcpSessionPoolEventReason Reason,
    DateTimeOffset OccurredAtUtc);
