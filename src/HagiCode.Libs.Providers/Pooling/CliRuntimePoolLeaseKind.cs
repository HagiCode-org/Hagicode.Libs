namespace HagiCode.Libs.Providers.Pooling;

internal enum CliRuntimePoolLeaseKind
{
    ColdStart = 0,
    WarmReuse = 1,
    CompatibilityReplacement = 2
}
