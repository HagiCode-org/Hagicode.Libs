using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.CiSetup.Console;
using Shouldly;

namespace HagiCode.Libs.CiSetup.Tests;

public sealed class CiSetupDiscoveryVerifierTests
{
    private static readonly CliInstallDescriptor ClaudeCode = CliInstallRegistry.Descriptors.Single(static d => d.ProviderName == "ClaudeCode");
    private static readonly CliInstallStrategy NativeStrategy = ClaudeCode.InstallStrategies.Single(static s => s.Kind == CliInstallKind.NativeInstaller);
    private static readonly CliInstallStrategy NpmStrategy = ClaudeCode.InstallStrategies.Single(static s => s.Kind == CliInstallKind.Npm);

    [Test]
    public void Passes_when_path_source_kind_and_pinned_version_match()
    {
        var verification = CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            NativeStrategy,
            new CiSetupExpectations(ExpectedVersion: null, ExpectedInstallKind: CliInstallKind.NativeInstaller, RequireWellKnownSource: true),
            Found(CliDiscoverySource.WellKnownLocation, CliInstallKind.NativeInstaller, "2.1.168"));

        verification.Verdict.ShouldBe(CiSetupVerdict.Passed);
        verification.ExitCode.ShouldBe(0);
    }

    [Test]
    public void Passes_when_the_resolved_path_ends_in_the_expected_extension_case_insensitively()
    {
        var result = new CliDiscoveryResult("ClaudeCode")
        {
            ResolvedPath = @"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code-win32-x64\claude.EXE",
            Source = CliDiscoverySource.Path,
            InstallKind = CliInstallKind.Npm,
            Version = "2.1.168",
            VersionFailure = CliVersionProbeFailure.None
        };

        var verification = CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            NpmStrategy,
            new CiSetupExpectations(null, null, RequireWellKnownSource: false, ExpectedExtension: ".exe"),
            result);

        verification.Verdict.ShouldBe(CiSetupVerdict.Passed);
    }

    [Test]
    public void Reports_extension_mismatch_when_a_command_script_is_resolved_for_a_native_lane()
    {
        var result = new CliDiscoveryResult("ClaudeCode")
        {
            ResolvedPath = @"C:\Users\u\AppData\Roaming\npm\claude.cmd",
            Source = CliDiscoverySource.Path,
            InstallKind = CliInstallKind.Npm,
            Version = "2.1.168",
            VersionFailure = CliVersionProbeFailure.None
        };

        var verification = CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            NpmStrategy,
            new CiSetupExpectations(null, null, RequireWellKnownSource: false, ExpectedExtension: ".exe"),
            result);

        verification.Verdict.ShouldBe(CiSetupVerdict.ExtensionMismatch);
        verification.ExitCode.ShouldBe(7);
    }

    [Test]
    public void Reports_not_found_when_nothing_resolves()
    {
        var verification = Evaluate(new CliDiscoveryResult("ClaudeCode"), NativeStrategy);

        verification.Verdict.ShouldBe(CiSetupVerdict.NotFound);
        verification.Summary.ShouldContain("not found");
        verification.ExitCode.ShouldBe(3);
    }

    [Test]
    public void Reports_not_found_when_an_off_path_lane_resolves_through_the_path()
    {
        var verification = CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            NativeStrategy,
            new CiSetupExpectations(null, CliInstallKind.NativeInstaller, RequireWellKnownSource: true),
            Found(CliDiscoverySource.Path, CliInstallKind.NativeInstaller, "2.1.168"));

        verification.Verdict.ShouldBe(CiSetupVerdict.NotFound);
    }

    [Test]
    public void Reports_version_unavailable_with_the_last_probe_failure()
    {
        var result = new CliDiscoveryResult("ClaudeCode")
        {
            ResolvedPath = "/home/u/.local/bin/claude",
            Source = CliDiscoverySource.WellKnownLocation,
            InstallKind = CliInstallKind.NativeInstaller,
            VersionFailure = CliVersionProbeFailure.TimedOut
        };

        var verification = Evaluate(result, NativeStrategy);

        verification.Verdict.ShouldBe(CiSetupVerdict.VersionUnavailable);
        verification.Summary.ShouldContain("TimedOut");
        verification.ExitCode.ShouldBe(4);
    }

    [Test]
    public void Reports_version_mismatch_for_an_explicit_expected_version()
    {
        var verification = CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            NativeStrategy,
            new CiSetupExpectations("9.9.9", null, RequireWellKnownSource: false),
            Found(CliDiscoverySource.Path, CliInstallKind.NativeInstaller, "2.1.168"));

        verification.Verdict.ShouldBe(CiSetupVerdict.VersionMismatch);
        verification.Summary.ShouldContain("expected 9.9.9, observed 2.1.168");
        verification.ExitCode.ShouldBe(5);
    }

    [Test]
    public void Reports_version_mismatch_against_the_registry_pin_for_a_pinnable_strategy()
    {
        var verification = Evaluate(Found(CliDiscoverySource.Path, CliInstallKind.Npm, "2.1.1"), NpmStrategy);

        verification.Verdict.ShouldBe(CiSetupVerdict.VersionMismatch);
        verification.Summary.ShouldContain("expected 2.1.168");
    }

    [Test]
    public void Accepts_any_well_formed_version_and_logs_drift_when_the_strategy_cannot_pin()
    {
        var verification = Evaluate(Found(CliDiscoverySource.WellKnownLocation, CliInstallKind.Homebrew, "2.2.0"), null);

        verification.Verdict.ShouldBe(CiSetupVerdict.Passed);
        verification.Notes.ShouldContain(note => note.Contains("version drift", StringComparison.Ordinal));
    }

    [Test]
    public void Reports_install_kind_mismatch_when_the_lane_would_pass_on_a_different_install()
    {
        var verification = CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            NativeStrategy,
            new CiSetupExpectations(null, CliInstallKind.NativeInstaller, RequireWellKnownSource: false),
            Found(CliDiscoverySource.Path, CliInstallKind.Npm, "2.1.168"));

        verification.Verdict.ShouldBe(CiSetupVerdict.InstallKindMismatch);
        verification.Summary.ShouldContain("expected NativeInstaller, observed Npm");
        verification.ExitCode.ShouldBe(6);
    }

    [Test]
    public void Reports_install_kind_mismatch_when_attribution_is_unknown()
    {
        var verification = CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            NativeStrategy,
            new CiSetupExpectations(null, CliInstallKind.NativeInstaller, RequireWellKnownSource: false),
            Found(CliDiscoverySource.Path, CliInstallKind.Unknown, "2.1.168"));

        verification.Verdict.ShouldBe(CiSetupVerdict.InstallKindMismatch);
    }

    private static CiSetupVerification Evaluate(CliDiscoveryResult result, CliInstallStrategy? strategy)
    {
        return CiSetupDiscoveryVerifier.Evaluate(
            ClaudeCode,
            strategy,
            new CiSetupExpectations(null, null, RequireWellKnownSource: false),
            result);
    }

    private static CliDiscoveryResult Found(CliDiscoverySource source, CliInstallKind kind, string version)
    {
        return new CliDiscoveryResult("ClaudeCode")
        {
            ResolvedPath = "/home/u/.local/bin/claude",
            Source = source,
            InstallKind = kind,
            Version = version,
            VersionFailure = CliVersionProbeFailure.None
        };
    }
}
