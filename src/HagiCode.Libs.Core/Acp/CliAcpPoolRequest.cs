namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes one pooled ACP acquire request.
/// </summary>
/// <param name="ProviderName">The provider identity.</param>
/// <param name="LogicalSessionKey">The logical session key requested by the caller, when available.</param>
/// <param name="CompatibilityFingerprint">The compatibility fingerprint used to validate warm reuse.</param>
/// <param name="Settings">The effective provider pool settings.</param>
public sealed record CliAcpPoolRequest(
    string ProviderName,
    string? LogicalSessionKey,
    string CompatibilityFingerprint,
    CliPoolSettings Settings);
