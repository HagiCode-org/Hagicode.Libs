namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Filters alive managed processes. A <see langword="null" /> field matches every value.
/// </summary>
public sealed record CliManagedProcessQuery
{
    /// <summary>
    /// Gets the provider name to match, or <see langword="null" /> for any provider.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Gets the session identifier to match, or <see langword="null" /> for any session.
    /// </summary>
    public string? SessionId { get; init; }
}
