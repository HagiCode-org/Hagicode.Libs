using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.CiSetup.Console;

/// <summary>
/// The outcome category of a discovery verification.
/// </summary>
public enum CiSetupVerdict
{
    /// <summary>Discovery found the CLI and every expectation held.</summary>
    Passed,

    /// <summary>No executable was resolved, or it was not resolved through the required location.</summary>
    NotFound,

    /// <summary>The executable was found but its version could not be read.</summary>
    VersionUnavailable,

    /// <summary>The observed version differs from the expected version.</summary>
    VersionMismatch,

    /// <summary>The observed install kind differs from the expected install kind.</summary>
    InstallKindMismatch,

    /// <summary>The resolved path does not end in the expected file extension.</summary>
    ExtensionMismatch
}

/// <summary>
/// The expectations a discovery verification asserts.
/// </summary>
/// <param name="ExpectedVersion">The exact version to assert, or <see langword="null" /> to accept any well-formed version.</param>
/// <param name="ExpectedInstallKind">The install kind to assert, or <see langword="null" /> to skip the assertion.</param>
/// <param name="RequireWellKnownSource">Whether the executable must come from a well-known location (used for off-PATH lanes).</param>
/// <param name="ExpectedExtension">The file extension the resolved path must end in, such as <c>.exe</c>, or <see langword="null" /> to skip the assertion.</param>
public sealed record CiSetupExpectations(
    string? ExpectedVersion,
    CliInstallKind? ExpectedInstallKind,
    bool RequireWellKnownSource,
    string? ExpectedExtension = null);

/// <summary>
/// The result of evaluating a discovery result against expectations.
/// </summary>
/// <param name="Verdict">The outcome category.</param>
/// <param name="Summary">A one-line explanation that names the failing stage.</param>
/// <param name="Notes">Informational lines, such as version drift on a strategy that cannot pin.</param>
public sealed record CiSetupVerification(CiSetupVerdict Verdict, string Summary, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Gets the process exit code for this verdict.
    /// </summary>
    public int ExitCode => Verdict switch
    {
        CiSetupVerdict.Passed => 0,
        CiSetupVerdict.NotFound => 3,
        CiSetupVerdict.VersionUnavailable => 4,
        CiSetupVerdict.VersionMismatch => 5,
        CiSetupVerdict.InstallKindMismatch => 6,
        CiSetupVerdict.ExtensionMismatch => 7,
        _ => 1
    };
}

/// <summary>
/// Evaluates a discovery result against the expectations of one lane. Pure, so it is unit-tested without processes.
/// </summary>
public static class CiSetupDiscoveryVerifier
{
    /// <summary>
    /// Evaluates a discovery result.
    /// </summary>
    /// <param name="descriptor">The provider descriptor.</param>
    /// <param name="strategy">The strategy under test, or <see langword="null" /> when none was selected.</param>
    /// <param name="expectations">The expectations to assert.</param>
    /// <param name="result">The discovery result.</param>
    /// <returns>The verification outcome.</returns>
    public static CiSetupVerification Evaluate(
        CliInstallDescriptor descriptor,
        CliInstallStrategy? strategy,
        CiSetupExpectations expectations,
        CliDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(result);

        if (!result.IsFound)
        {
            return Fail(CiSetupVerdict.NotFound, "not found: no executable was resolved for the provider's candidates.");
        }

        if (expectations.RequireWellKnownSource && result.Source != CliDiscoverySource.WellKnownLocation)
        {
            return Fail(
                CiSetupVerdict.NotFound,
                $"not found: resolved through {result.Source}, but the install directory was removed from PATH and only the well-known location was expected.");
        }

        if (expectations.ExpectedExtension is { } expectedExtension
            && !string.Equals(Path.GetExtension(result.ResolvedPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                CiSetupVerdict.ExtensionMismatch,
                $"extension mismatch: expected {expectedExtension}, resolved {result.ResolvedPath}.");
        }

        if (string.IsNullOrWhiteSpace(result.Version))
        {
            return Fail(
                CiSetupVerdict.VersionUnavailable,
                $"version unavailable: last probe failure was {result.VersionFailure?.ToString() ?? "not probed"}.");
        }

        var notes = new List<string>();
        var expectedVersion = expectations.ExpectedVersion
                              ?? (strategy is { SupportsVersionPin: true } && !string.IsNullOrWhiteSpace(descriptor.PinnedVersion)
                                  ? descriptor.PinnedVersion
                                  : null);

        if (expectedVersion is not null)
        {
            if (!string.Equals(result.Version, expectedVersion, StringComparison.Ordinal))
            {
                return Fail(
                    CiSetupVerdict.VersionMismatch,
                    $"version mismatch: expected {expectedVersion}, observed {result.Version}.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(descriptor.PinnedVersion)
                 && !string.Equals(result.Version, descriptor.PinnedVersion, StringComparison.Ordinal))
        {
            notes.Add($"version drift: observed {result.Version}, registry pin {descriptor.PinnedVersion} (the strategy cannot pin, so any well-formed version passes).");
        }

        if (expectations.ExpectedInstallKind is { } expectedKind && result.InstallKind != expectedKind)
        {
            return Fail(
                CiSetupVerdict.InstallKindMismatch,
                $"install kind mismatch: expected {expectedKind}, observed {result.InstallKind}.");
        }

        return new CiSetupVerification(CiSetupVerdict.Passed, "discovery verified.", notes);
    }

    private static CiSetupVerification Fail(CiSetupVerdict verdict, string summary) =>
        new(verdict, summary, []);
}
