using System.Runtime.InteropServices;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliDiscoveryWindowsPolicyTests
{
    [Test]
    public async Task DiscoverAsync_on_windows_reports_native_executable_missing_when_only_a_shim_exists_for_claude()
    {
        using var sandbox = new PolicyDiscoverySandbox();
        var shim = sandbox.CreateFile("npm", "claude.cmd");

        var result = await CreateService(Architecture.X64).DiscoverAsync(
            ClaudeCodeDescriptor(),
            new CliDiscoveryOptions { VerifyVersion = false, EnvironmentVariables = sandbox.Env(path: sandbox.Dir("npm")) });

        result.IsFound.ShouldBeFalse();
        result.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NativeExecutableMissing);
        result.SkippedCommandScripts.ShouldBe([shim]);
    }

    [Test]
    public async Task DiscoverAsync_on_windows_reports_not_installed_when_nothing_exists()
    {
        using var sandbox = new PolicyDiscoverySandbox();

        var result = await CreateService(Architecture.X64).DiscoverAsync(
            ClaudeCodeDescriptor(),
            new CliDiscoveryOptions { VerifyVersion = false, EnvironmentVariables = sandbox.Env(path: sandbox.Dir("empty")) });

        result.IsFound.ShouldBeFalse();
        result.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NotInstalled);
        result.SkippedCommandScripts.ShouldBeEmpty();
    }

    [Test]
    public async Task DiscoverAsync_on_windows_finds_the_packaged_claude_binary_behind_the_shim_and_attributes_it_to_npm()
    {
        using var sandbox = new PolicyDiscoverySandbox();
        var npm = sandbox.Dir("npm");
        sandbox.CreateFile("npm", "claude.cmd");
        var packaged = sandbox.CreateFile("npm", "node_modules", "@anthropic-ai", "claude-code-win32-x64", "claude.exe");

        var result = await CreateService(Architecture.X64).DiscoverAsync(
            ClaudeCodeDescriptor(),
            new CliDiscoveryOptions { VerifyVersion = false, EnvironmentVariables = sandbox.Env(path: npm) });

        result.IsFound.ShouldBeTrue();
        result.ResolvedPath.ShouldBe(packaged);
        result.NotFoundReason.ShouldBeNull();
        result.InstallKind.ShouldBe(CliInstallKind.Npm);
    }

    [Test]
    public async Task DiscoverAsync_on_windows_honors_an_explicitly_configured_command_script()
    {
        using var sandbox = new PolicyDiscoverySandbox();
        var configured = sandbox.CreateFile("tools", "claude.cmd");

        var result = await CreateService(Architecture.X64).DiscoverAsync(
            ClaudeCodeDescriptor(),
            new CliDiscoveryOptions
            {
                ConfiguredExecutablePath = configured,
                VerifyVersion = false,
                EnvironmentVariables = sandbox.Env(path: sandbox.Dir("empty")),
            });

        result.IsFound.ShouldBeTrue();
        result.ResolvedPath.ShouldBe(configured);
        result.Source.ShouldBe(CliDiscoverySource.Configured);
    }

    [Test]
    public async Task DiscoverAsync_on_windows_still_selects_a_script_only_agent_such_as_pi()
    {
        using var sandbox = new PolicyDiscoverySandbox();
        var cmd = sandbox.CreateFile("npm", "pi.cmd");
        var descriptor = CliInstallRegistry.FindByProviderName("Pi")!;

        var result = await CreateService(Architecture.X64).DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { VerifyVersion = false, EnvironmentVariables = sandbox.Env(path: sandbox.Dir("npm")) });

        result.IsFound.ShouldBeTrue();
        result.ResolvedPath.ShouldBe(cmd);
        result.NotFoundReason.ShouldBeNull();
    }

    private static CliInstallDescriptor ClaudeCodeDescriptor() => CliInstallRegistry.FindByProviderName("ClaudeCode")!;

    private static CliDiscoveryService CreateService(Architecture architecture)
    {
        return new CliDiscoveryService(
            new CliExecutableResolver(static () => true, () => architecture),
            new CliProcessManager(),
            runtimeEnvironmentResolver: null,
            platform: static () => CliPlatform.Windows);
    }

    private sealed class PolicyDiscoverySandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"discovery policy {Guid.NewGuid():N}");

        public PolicyDiscoverySandbox()
        {
            Directory.CreateDirectory(_root);
        }

        public string Dir(params string[] segments)
        {
            var path = Path.Combine([_root, .. segments]);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(params string[] segments)
        {
            var path = Path.Combine([_root, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return Path.GetFullPath(path);
        }

        public Dictionary<string, string?> Env(string? path = null)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PATH"] = path ?? _root,
                ["APPDATA"] = Path.Combine(_root, "appdata"),
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
