using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliInstallRegistryStrategyTests
{
    [Test]
    public void ClaudeCode_declares_the_npm_native_homebrew_winget_and_legacy_strategies()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "ClaudeCode");

        descriptor.InstallStrategies.Select(static strategy => strategy.Kind).ShouldBe(
        [
            CliInstallKind.Npm,
            CliInstallKind.NativeInstaller,
            CliInstallKind.Homebrew,
            CliInstallKind.WinGet,
            CliInstallKind.LegacyLocal
        ]);
    }

    [Test]
    public void ClaudeCode_strategies_declare_their_platforms_and_directories()
    {
        var strategies = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "ClaudeCode").InstallStrategies;

        var npm = strategies.Single(static strategy => strategy.Kind == CliInstallKind.Npm);
        npm.Platforms.ShouldBe(CliPlatform.All);
        npm.WellKnownDirectories.ShouldBe([@"%APPDATA%\npm"]);
        npm.SupportsVersionPin.ShouldBeTrue();

        var native = strategies.Single(static strategy => strategy.Kind == CliInstallKind.NativeInstaller);
        native.Platforms.ShouldBe(CliPlatform.All);
        native.WellKnownDirectories.ShouldBe(["~/.local/bin"]);

        var homebrew = strategies.Single(static strategy => strategy.Kind == CliInstallKind.Homebrew);
        homebrew.Platforms.ShouldBe(CliPlatform.MacOS);
        homebrew.WellKnownDirectories.ShouldBe(["/opt/homebrew/bin", "/usr/local/bin"]);

        var winget = strategies.Single(static strategy => strategy.Kind == CliInstallKind.WinGet);
        winget.Platforms.ShouldBe(CliPlatform.Windows);
        winget.WellKnownDirectories.ShouldBe([@"%LOCALAPPDATA%\Microsoft\WinGet\Links"]);

        var legacy = strategies.Single(static strategy => strategy.Kind == CliInstallKind.LegacyLocal);
        legacy.WellKnownDirectories.ShouldBe(["~/.claude/local"]);
    }

    [Test]
    public void Copilot_declares_version_argument_sets_in_core_order()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Copilot");

        descriptor.VersionArgumentSets.Count.ShouldBe(3);
        descriptor.VersionArgumentSets[0].ShouldBe(["version"]);
        descriptor.VersionArgumentSets[1].ShouldBe(["--version"]);
        descriptor.VersionArgumentSets[2].ShouldBe(["-v"]);
    }

    [Test]
    public void Descriptors_without_declared_metadata_use_the_defaults()
    {
        var kimi = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Kimi");

        kimi.VersionArgumentSets.Count.ShouldBe(1);
        kimi.VersionArgumentSets[0].ShouldBe(["--version"]);
        kimi.InstallStrategies.ShouldBeEmpty();
    }
}
