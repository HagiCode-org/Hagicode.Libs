using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.CiSetup.Console;
using Shouldly;

namespace HagiCode.Libs.CiSetup.Tests;

public sealed class CiSetupStrategiesTests
{
    private static readonly CliInstallDescriptor ClaudeCode = CliInstallRegistry.Descriptors.Single(static d => d.ProviderName == "ClaudeCode");

    [Test]
    public void TryParseKind_accepts_the_documented_aliases()
    {
        CiSetupStrategies.TryParseKind("npm", out var npm).ShouldBeTrue();
        npm.ShouldBe(CliInstallKind.Npm);
        CiSetupStrategies.TryParseKind("native", out var native).ShouldBeTrue();
        native.ShouldBe(CliInstallKind.NativeInstaller);
        CiSetupStrategies.TryParseKind("NativeInstaller", out var nativeName).ShouldBeTrue();
        nativeName.ShouldBe(CliInstallKind.NativeInstaller);
        CiSetupStrategies.TryParseKind("brew", out var brew).ShouldBeTrue();
        brew.ShouldBe(CliInstallKind.Homebrew);
        CiSetupStrategies.TryParseKind("winget", out var winget).ShouldBeTrue();
        winget.ShouldBe(CliInstallKind.WinGet);
        CiSetupStrategies.TryParseKind("bogus", out _).ShouldBeFalse();
    }

    [Test]
    public void Npm_installer_requests_the_pinned_version_when_the_strategy_can_pin()
    {
        var command = Build(CliInstallKind.Npm, CliPlatform.Linux, ClaudeCode, npmExecutable: "/usr/bin/npm");

        command.FileName.ShouldBe("/usr/bin/npm");
        command.Arguments.ShouldBe(["install", "--global", "@anthropic-ai/claude-code@2.1.168"]);
    }

    [Test]
    public void Native_installer_on_unix_pins_through_the_script_version_argument()
    {
        var command = Build(CliInstallKind.NativeInstaller, CliPlatform.Linux, ClaudeCode);

        command.FileName.ShouldBe("bash");
        command.Arguments[^1].ShouldBe($"curl -fsSL {CiSetupStrategies.NativeUnixInstallerUrl} | bash -s 2.1.168");
        command.Arguments.Take(2).ToArray().ShouldBe(["-o", "pipefail"]);
    }

    [Test]
    public void Native_installer_on_windows_passes_the_pinned_version_to_the_scriptblock()
    {
        var command = Build(CliInstallKind.NativeInstaller, CliPlatform.Windows, ClaudeCode);

        command.FileName.ShouldBe("powershell");
        command.Arguments[^1].ShouldContain(CiSetupStrategies.NativeWindowsInstallerUrl);
        command.Arguments[^1].ShouldEndWith(" 2.1.168");
    }

    [Test]
    public void Native_installer_refuses_a_version_that_is_not_a_plain_semantic_version()
    {
        var descriptor = new CliInstallDescriptor("Probe", "", "2.1.168; rm -rf /", ["probe"], IsPubliclyInstallable: false)
        {
            InstallStrategies = [new CliInstallStrategy(CliInstallKind.NativeInstaller, CliPlatform.All, ["~/.local/bin"], SupportsVersionPin: true)],
        };
        var installer = CiSetupStrategies.FindInstaller(CliInstallKind.NativeInstaller)!;

        Should.Throw<ArgumentException>(() => installer.CreateCommand(new CliInstallRequest(
            descriptor,
            descriptor.InstallStrategies[0],
            CliPlatform.Linux,
            NpmExecutable: null)));
    }

    [Test]
    public void Homebrew_installer_installs_the_claude_code_cask()
    {
        var command = Build(CliInstallKind.Homebrew, CliPlatform.MacOS, ClaudeCode);

        command.FileName.ShouldBe("brew");
        command.Arguments.ShouldBe(["install", "--cask", "claude-code"]);
    }

    [Test]
    public void WinGet_installer_pins_with_the_documented_version_flag()
    {
        var command = Build(CliInstallKind.WinGet, CliPlatform.Windows, ClaudeCode);

        command.FileName.ShouldBe("winget");
        command.Arguments.ShouldContain("Anthropic.ClaudeCode");
        command.Arguments.Skip(command.Arguments.Count - 2).ShouldBe(["--version", "2.1.168"]);
    }

    [Test]
    public void Every_installer_disables_the_auto_updater()
    {
        foreach (var kind in new[] { CliInstallKind.Npm, CliInstallKind.NativeInstaller, CliInstallKind.Homebrew, CliInstallKind.WinGet })
        {
            var command = Build(kind, kind == CliInstallKind.WinGet ? CliPlatform.Windows : CliPlatform.Linux, ClaudeCode);

            command.EnvironmentVariables["DISABLE_AUTOUPDATER"].ShouldBe("1");
        }
    }

    [Test]
    public void Legacy_local_has_no_installer()
    {
        CiSetupStrategies.FindInstaller(CliInstallKind.LegacyLocal).ShouldBeNull();
    }

    private static CliInstallCommand Build(
        CliInstallKind kind,
        CliPlatform platform,
        CliInstallDescriptor descriptor,
        string? npmExecutable = null)
    {
        var strategy = descriptor.InstallStrategies.Single(candidate => candidate.Kind == kind);
        var installer = CiSetupStrategies.FindInstaller(kind)!;
        return installer.CreateCommand(new CliInstallRequest(descriptor, strategy, platform, npmExecutable));
    }
}
