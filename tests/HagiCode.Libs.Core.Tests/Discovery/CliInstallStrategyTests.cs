using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliInstallStrategyTests
{
    [Test]
    public void GetWellKnownDirectories_expands_tilde_from_home_on_unix()
    {
        var strategy = new CliInstallStrategy(CliInstallKind.NativeInstaller, CliPlatform.All, ["~/.local/bin"], SupportsVersionPin: false);

        var directories = strategy.GetWellKnownDirectories(
            CliPlatform.Linux,
            Env(("HOME", "/home/alice")));

        directories.ShouldBe(["/home/alice/.local/bin"]);
    }

    [Test]
    public void GetWellKnownDirectories_expands_tilde_from_user_profile_on_windows_and_normalizes_separators()
    {
        var strategy = new CliInstallStrategy(CliInstallKind.NativeInstaller, CliPlatform.All, ["~/.local/bin"], SupportsVersionPin: false);

        var directories = strategy.GetWellKnownDirectories(
            CliPlatform.Windows,
            Env(("USERPROFILE", @"C:\Users\alice"), ("HOME", "/ignored")));

        directories.ShouldBe([@"C:\Users\alice\.local\bin"]);
    }

    [Test]
    public void GetWellKnownDirectories_expands_windows_variables_case_insensitively()
    {
        var strategy = new CliInstallStrategy(
            CliInstallKind.WinGet,
            CliPlatform.Windows,
            [@"%LOCALAPPDATA%\Microsoft\WinGet\Links"],
            SupportsVersionPin: false);

        var directories = strategy.GetWellKnownDirectories(
            CliPlatform.Windows,
            Env(("localappdata", @"C:\Users\alice\AppData\Local")));

        directories.ShouldBe([@"C:\Users\alice\AppData\Local\Microsoft\WinGet\Links"]);
    }

    [Test]
    public void GetWellKnownDirectories_skips_templates_with_a_missing_variable()
    {
        var strategy = new CliInstallStrategy(
            CliInstallKind.Npm,
            CliPlatform.All,
            [@"%APPDATA%\npm", "~/.local/bin"],
            SupportsVersionPin: true);

        var directories = strategy.GetWellKnownDirectories(CliPlatform.Windows, Env(("USERPROFILE", @"C:\Users\alice")));

        directories.ShouldBe([@"C:\Users\alice\.local\bin"]);
    }

    [Test]
    public void GetWellKnownDirectories_uses_the_supplied_environment_not_the_host_environment()
    {
        var strategy = new CliInstallStrategy(CliInstallKind.LegacyLocal, CliPlatform.All, ["~/.claude/local"], SupportsVersionPin: false);

        var directories = strategy.GetWellKnownDirectories(CliPlatform.Linux, Env(("HOME", "/custom/home")));

        directories.ShouldBe(["/custom/home/.claude/local"]);
    }

    [Test]
    public void GetWellKnownDirectories_returns_nothing_for_a_strategy_on_another_platform()
    {
        var strategy = new CliInstallStrategy(CliInstallKind.Homebrew, CliPlatform.MacOS, ["/opt/homebrew/bin"], SupportsVersionPin: false);

        strategy.GetWellKnownDirectories(CliPlatform.Linux, Env()).ShouldBeEmpty();
        strategy.GetWellKnownDirectories(CliPlatform.Windows, Env()).ShouldBeEmpty();
        strategy.GetWellKnownDirectories(CliPlatform.MacOS, Env()).ShouldBe(["/opt/homebrew/bin"]);
    }

    [Test]
    public void AppliesTo_matches_combined_platform_flags()
    {
        var strategy = new CliInstallStrategy(CliInstallKind.Npm, CliPlatform.Unix, [], SupportsVersionPin: true);

        strategy.AppliesTo(CliPlatform.Linux).ShouldBeTrue();
        strategy.AppliesTo(CliPlatform.MacOS).ShouldBeTrue();
        strategy.AppliesTo(CliPlatform.Windows).ShouldBeFalse();
    }

    private static Dictionary<string, string?> Env(params (string Name, string? Value)[] entries)
    {
        return entries.ToDictionary(static entry => entry.Name, static entry => entry.Value, StringComparer.Ordinal);
    }
}
