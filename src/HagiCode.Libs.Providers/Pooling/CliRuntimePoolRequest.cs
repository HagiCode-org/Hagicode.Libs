using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Pooling;

internal sealed record CliRuntimePoolRequest(
    string ProviderName,
    string? LogicalSessionKey,
    string CompatibilityFingerprint,
    CliPoolSettings Settings);
