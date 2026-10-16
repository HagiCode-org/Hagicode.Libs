using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliDiscoveryServiceTests
{
    private const string ToolVersion = "1.2.3";

    [Test]
    public async Task DiscoverAsync_returns_path_source_kind_and_version_for_an_installed_cli()
    {
        using var sandbox = new DiscoverySandbox();
        var pathDir = sandbox.CreateDirectory("path");
        var tool = sandbox.WriteExecutable(pathDir, "tool", $"echo 'Tool {ToolVersion}'");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", pathDir)) });

        result.IsFound.ShouldBeTrue();
        result.ResolvedPath.ShouldBe(tool);
        result.Source.ShouldBe(CliDiscoverySource.Path);
        result.InstallKind.ShouldBe(CliInstallKind.Unknown);
        result.Version.ShouldBe(ToolVersion);
        result.VersionFailure.ShouldBe(CliVersionProbeFailure.None);
        result.ShadowedInstalls.ShouldBeEmpty();
    }

    [Test]
    public async Task DiscoverAsync_reports_not_found_with_probed_locations_and_does_not_throw()
    {
        using var sandbox = new DiscoverySandbox();
        var emptyDir = sandbox.CreateDirectory("empty");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", emptyDir)) });

        result.IsFound.ShouldBeFalse();
        result.ResolvedPath.ShouldBeNull();
        result.Source.ShouldBe(CliDiscoverySource.None);
        result.Version.ShouldBeNull();
        result.ProbedLocations.ShouldContain(Path.Combine(emptyDir, "tool"));
    }

    [Test]
    public async Task DiscoverAsync_reports_found_without_version_and_the_last_failure_category()
    {
        using var sandbox = new DiscoverySandbox();
        var pathDir = sandbox.CreateDirectory("path");
        var tool = sandbox.WriteExecutable(pathDir, "tool", "exit 2");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", pathDir)) });

        result.IsFound.ShouldBeTrue();
        result.ResolvedPath.ShouldBe(tool);
        result.Version.ShouldBeNull();
        result.VersionFailure.ShouldBe(CliVersionProbeFailure.CommandFailed);
    }

    [Test]
    public async Task DiscoverAsync_without_version_verification_resolves_the_path_without_starting_the_cli()
    {
        using var sandbox = new DiscoverySandbox();
        var pathDir = sandbox.CreateDirectory("path");
        var tool = sandbox.WriteExecutable(pathDir, "tool", $"echo 'Tool {ToolVersion}'");
        var manager = new CountingProcessManager();

        var result = await CreateService(manager).DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions
            {
                VerifyVersion = false,
                EnvironmentVariables = Env(("PATH", pathDir))
            });

        result.ResolvedPath.ShouldBe(tool);
        result.Version.ShouldBeNull();
        result.VersionFailure.ShouldBeNull();
        manager.ExecuteCount.ShouldBe(0);
    }

    [Test]
    public async Task DiscoverAsync_prefers_a_configured_path_over_path()
    {
        using var sandbox = new DiscoverySandbox();
        var configuredDir = sandbox.CreateDirectory("configured");
        var pathDir = sandbox.CreateDirectory("path");
        var configured = sandbox.WriteExecutable(configuredDir, "tool", $"echo '{ToolVersion}'");
        var onPath = sandbox.WriteExecutable(pathDir, "tool", "echo '9.9.9'");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions
            {
                ConfiguredExecutablePath = configured,
                EnvironmentVariables = Env(("PATH", pathDir))
            });

        result.ResolvedPath.ShouldBe(configured);
        result.Source.ShouldBe(CliDiscoverySource.Configured);
        result.Version.ShouldBe(ToolVersion);
        result.ShadowedInstalls.Select(static shadowed => shadowed.Path).ShouldBe([onPath]);
    }

    [Test]
    public async Task DiscoverAsync_falls_through_an_unresolvable_configured_path()
    {
        using var sandbox = new DiscoverySandbox();
        var pathDir = sandbox.CreateDirectory("path");
        var onPath = sandbox.WriteExecutable(pathDir, "tool", $"echo '{ToolVersion}'");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions
            {
                ConfiguredExecutablePath = Path.Combine(sandbox.RootPath, "missing", "tool"),
                EnvironmentVariables = Env(("PATH", pathDir))
            });

        result.IsFound.ShouldBeTrue();
        result.ResolvedPath.ShouldBe(onPath);
        result.Source.ShouldBe(CliDiscoverySource.Path);
    }

    [Test]
    public async Task DiscoverAsync_searches_hagicode_agent_cli_path_before_path()
    {
        using var sandbox = new DiscoverySandbox();
        var agentDir = sandbox.CreateDirectory("agent");
        var pathDir = sandbox.CreateDirectory("path");
        var fromAgent = sandbox.WriteExecutable(agentDir, "tool", $"echo '{ToolVersion}'");
        var onPath = sandbox.WriteExecutable(pathDir, "tool", "echo '9.9.9'");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions
            {
                EnvironmentVariables = Env(("HAGICODE_AGENT_CLI_PATH", agentDir), ("PATH", pathDir))
            });

        result.ResolvedPath.ShouldBe(fromAgent);
        result.Source.ShouldBe(CliDiscoverySource.AgentCliPath);
        result.ShadowedInstalls.Select(static shadowed => shadowed.Path).ShouldBe([onPath]);
    }

    [Test]
    public async Task DiscoverAsync_finds_a_well_known_native_install_when_its_directory_is_not_on_path()
    {
        using var sandbox = new DiscoverySandbox();
        var home = sandbox.CreateDirectory("home");
        var nativeDir = sandbox.CreateDirectory("home", ".local", "bin");
        var native = sandbox.WriteExecutable(nativeDir, "tool", $"echo '{ToolVersion}'");
        var emptyDir = sandbox.CreateDirectory("empty");
        var descriptor = Descriptor(strategies:
        [
            new CliInstallStrategy(CliInstallKind.NativeInstaller, CliPlatform.All, ["~/.local/bin"], SupportsVersionPin: false)
        ]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", emptyDir), ("HOME", home)) });

        result.ResolvedPath.ShouldBe(native);
        result.Source.ShouldBe(CliDiscoverySource.WellKnownLocation);
        result.InstallKind.ShouldBe(CliInstallKind.NativeInstaller);
        result.Version.ShouldBe(ToolVersion);
    }

    [Test]
    public async Task DiscoverAsync_never_lets_a_well_known_location_override_a_path_hit()
    {
        using var sandbox = new DiscoverySandbox();
        var home = sandbox.CreateDirectory("home");
        var nativeDir = sandbox.CreateDirectory("home", ".local", "bin");
        var pathDir = sandbox.CreateDirectory("path");
        var onPath = sandbox.WriteExecutable(pathDir, "tool", $"echo '{ToolVersion}'");
        var native = sandbox.WriteExecutable(nativeDir, "tool", "echo '9.9.9'");
        var descriptor = Descriptor(strategies:
        [
            new CliInstallStrategy(CliInstallKind.NativeInstaller, CliPlatform.All, ["~/.local/bin"], SupportsVersionPin: false)
        ]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", pathDir), ("HOME", home)) });

        result.ResolvedPath.ShouldBe(onPath);
        result.Source.ShouldBe(CliDiscoverySource.Path);
        result.ShadowedInstalls.Single().Path.ShouldBe(native);
        result.ShadowedInstalls.Single().Source.ShouldBe(CliDiscoverySource.WellKnownLocation);
        result.ShadowedInstalls.Single().VersionFailure.ShouldBeNull();
    }

    [Test]
    public async Task DiscoverAsync_ignores_strategies_for_another_platform()
    {
        using var sandbox = new DiscoverySandbox();
        var brewDir = sandbox.CreateDirectory("brew");
        sandbox.WriteExecutable(brewDir, "tool", $"echo '{ToolVersion}'");
        var emptyDir = sandbox.CreateDirectory("empty");
        var descriptor = Descriptor(strategies:
        [
            new CliInstallStrategy(CliInstallKind.Homebrew, CliPlatform.MacOS, [brewDir], SupportsVersionPin: false)
        ]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", emptyDir)) });

        result.IsFound.ShouldBeFalse();
        result.ProbedLocations.ShouldNotContain(Path.Combine(brewDir, "tool"));
    }

    [Test]
    public async Task DiscoverAsync_falls_back_to_the_next_candidate_and_reports_the_failed_one_as_shadowed()
    {
        using var sandbox = new DiscoverySandbox();
        var firstDir = sandbox.CreateDirectory("first");
        var secondDir = sandbox.CreateDirectory("second");
        var broken = sandbox.WriteExecutable(firstDir, "tool", "exit 2");
        var working = sandbox.WriteExecutable(secondDir, "tool", $"echo '{ToolVersion}'");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", string.Join(Path.PathSeparator, firstDir, secondDir))) });

        result.ResolvedPath.ShouldBe(working);
        result.Version.ShouldBe(ToolVersion);
        result.VersionFailure.ShouldBe(CliVersionProbeFailure.None);
        var shadowed = result.ShadowedInstalls.Single();
        shadowed.Path.ShouldBe(broken);
        shadowed.VersionFailure.ShouldBe(CliVersionProbeFailure.CommandFailed);
    }

    [Test]
    public async Task DiscoverAsync_collapses_duplicate_paths_reachable_through_several_lookup_directories()
    {
        using var sandbox = new DiscoverySandbox();
        var sharedDir = sandbox.CreateDirectory("shared");
        var tool = sandbox.WriteExecutable(sharedDir, "tool", $"echo '{ToolVersion}'");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions
            {
                EnvironmentVariables = Env(("HAGICODE_AGENT_CLI_PATH", sharedDir), ("PATH", sharedDir))
            });

        result.ResolvedPath.ShouldBe(tool);
        result.ShadowedInstalls.ShouldBeEmpty();
    }

    [Test]
    public async Task DiscoverAsync_prefers_the_copilot_preferred_executable_over_a_vscode_shim()
    {
        using var sandbox = new DiscoverySandbox();
        var shimDir = sandbox.CreateDirectory("user", "globalstorage", "github.copilot-chat", "copilotcli");
        var realDir = sandbox.CreateDirectory("real");
        var shim = sandbox.WriteExecutable(shimDir, "copilot", $"echo '{ToolVersion}'");
        var real = sandbox.WriteExecutable(realDir, "copilot", $"echo '{ToolVersion}'");
        var descriptor = Descriptor("Copilot", ["copilot"]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", string.Join(Path.PathSeparator, shimDir, realDir))) });

        result.ResolvedPath.ShouldBe(real);
        result.ShadowedInstalls.Single().Path.ShouldBe(shim);
    }

    [Test]
    public async Task DiscoverAsync_for_kiro_selects_only_kiro_cli_names()
    {
        using var sandbox = new DiscoverySandbox();
        var pathDir = sandbox.CreateDirectory("path");
        sandbox.WriteExecutable(pathDir, "kiro", $"echo '{ToolVersion}'");
        var kiroCli = sandbox.WriteExecutable(pathDir, "kiro-cli", $"echo '{ToolVersion}'");
        var descriptor = Descriptor("Kiro", ["kiro", "kiro-cli"]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", pathDir)) });

        result.ResolvedPath.ShouldBe(kiroCli);
        result.ShadowedInstalls.ShouldBeEmpty();
    }

    [Test]
    public async Task DiscoverAsync_for_kiro_reports_not_found_when_only_a_bare_kiro_exists()
    {
        using var sandbox = new DiscoverySandbox();
        var pathDir = sandbox.CreateDirectory("path");
        sandbox.WriteExecutable(pathDir, "kiro", $"echo '{ToolVersion}'");
        var descriptor = Descriptor("Kiro", ["kiro", "kiro-cli"]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", pathDir)) });

        result.IsFound.ShouldBeFalse();
    }

    [Test]
    public async Task DiscoverAsync_uses_the_supplied_environment_for_the_version_probe()
    {
        using var sandbox = new DiscoverySandbox();
        var pathDir = sandbox.CreateDirectory("path");
        sandbox.WriteExecutable(pathDir, "tool", "if [ \"$DISCOVERY_PROBE\" = \"from-discovery\" ]; then echo '1.2.3'; else exit 2; fi");

        var result = await CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions
            {
                EnvironmentVariables = Env(("PATH", pathDir), ("DISCOVERY_PROBE", "from-discovery"))
            });

        result.Version.ShouldBe("1.2.3");
    }

    [Test]
    public async Task DiscoverAsync_attributes_an_npm_install_through_the_symlink_target()
    {
        using var sandbox = new DiscoverySandbox();
        var packageDir = sandbox.CreateDirectory("prefix", "lib", "node_modules", "@anthropic-ai", "claude-code");
        var entry = sandbox.WriteExecutable(packageDir, "cli.js", $"echo '{ToolVersion}'");
        var binDir = sandbox.CreateDirectory("prefix", "bin");
        var link = Path.Combine(binDir, "tool");
        File.CreateSymbolicLink(link, entry);
        var descriptor = Descriptor(npmPackage: "@anthropic-ai/claude-code", strategies:
        [
            new CliInstallStrategy(CliInstallKind.Npm, CliPlatform.All, [], SupportsVersionPin: true)
        ]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", binDir)) });

        result.ResolvedPath.ShouldBe(link);
        result.InstallKind.ShouldBe(CliInstallKind.Npm);
        result.Version.ShouldBe(ToolVersion);
    }

    [Test]
    public async Task DiscoverAsync_attributes_an_npm_install_from_a_node_modules_directory_next_to_the_executable()
    {
        using var sandbox = new DiscoverySandbox();
        var prefixDir = sandbox.CreateDirectory("prefix");
        sandbox.CreateDirectory("prefix", "node_modules", "@anthropic-ai", "claude-code");
        var tool = sandbox.WriteExecutable(prefixDir, "tool", $"echo '{ToolVersion}'");
        var descriptor = Descriptor(npmPackage: "@anthropic-ai/claude-code", strategies:
        [
            new CliInstallStrategy(CliInstallKind.Npm, CliPlatform.All, [], SupportsVersionPin: true)
        ]);

        var result = await CreateService().DiscoverAsync(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", prefixDir)) });

        result.ResolvedPath.ShouldBe(tool);
        result.InstallKind.ShouldBe(CliInstallKind.Npm);
    }

    [Test]
    public async Task DiscoverAllAsync_isolates_a_timed_out_provider_from_the_others()
    {
        using var sandbox = new DiscoverySandbox();
        var slowDir = sandbox.CreateDirectory("slow");
        var fastDir = sandbox.CreateDirectory("fast");
        sandbox.WriteExecutable(slowDir, "slow", "/bin/sleep 30");
        sandbox.WriteExecutable(fastDir, "fast", $"echo '{ToolVersion}'");
        var slow = Descriptor("Slow", ["slow"]);
        var fast = Descriptor("Fast", ["fast"]);

        var results = await CreateService().DiscoverAllAsync(
            [slow, fast],
            new CliDiscoveryOptions
            {
                VersionProbeTimeout = TimeSpan.FromMilliseconds(300),
                EnvironmentVariables = Env(("PATH", string.Join(Path.PathSeparator, slowDir, fastDir)))
            });

        results.Count.ShouldBe(2);
        results[0].ProviderName.ShouldBe("Slow");
        results[0].VersionFailure.ShouldBe(CliVersionProbeFailure.TimedOut);
        results[1].ProviderName.ShouldBe("Fast");
        results[1].Version.ShouldBe(ToolVersion);
    }

    [Test]
    public async Task DiscoverAsync_propagates_caller_cancellation()
    {
        using var sandbox = new DiscoverySandbox();
        var slowDir = sandbox.CreateDirectory("slow");
        sandbox.WriteExecutable(slowDir, "tool", "/bin/sleep 30");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", slowDir)) },
            cancellation.Token));
    }

    [Test]
    public async Task DiscoverAsync_cancelled_before_start_throws_without_probing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => CreateService().DiscoverAsync(
            Descriptor(),
            new CliDiscoveryOptions { EnvironmentVariables = Env(("PATH", string.Empty)) },
            cancellation.Token));
    }

    [Test]
    public async Task Usage_example_from_the_guide_compiles_and_runs()
    {
        // The statements from docs/agent-cli-discovery-guide.md. The empty PATH keeps this test from starting any CLI.
        var service = new CliDiscoveryService(new CliExecutableResolver(), new CliProcessManager());
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "ClaudeCode");

        var result = await service.DiscoverAsync(descriptor, new CliDiscoveryOptions
        {
            VerifyVersion = true,
            EnvironmentVariables = new Dictionary<string, string?> { ["PATH"] = string.Empty }
        });

        var line = result.IsFound
            ? $"{result.ResolvedPath} source={result.Source} kind={result.InstallKind} version={result.Version}"
            : $"Not found. Probed {result.ProbedLocations.Count} locations.";
        line.ShouldNotBeNullOrWhiteSpace();
    }

    private static CliDiscoveryService CreateService(CliProcessManager? processManager = null)
    {
        return new CliDiscoveryService(
            new CliExecutableResolver(static () => false),
            processManager ?? new CliProcessManager(),
            runtimeEnvironmentResolver: null,
            platform: static () => CliPlatform.Linux);
    }

    private static CliInstallDescriptor Descriptor(
        string providerName = "Tool",
        string[]? candidates = null,
        string npmPackage = "",
        CliInstallStrategy[]? strategies = null)
    {
        return new CliInstallDescriptor(
            ProviderName: providerName,
            NpmPackage: npmPackage,
            PinnedVersion: string.Empty,
            ExecutableCandidates: candidates ?? ["tool"],
            IsPubliclyInstallable: false)
        {
            InstallStrategies = strategies ?? [],
        };
    }

    private static Dictionary<string, string?> Env(params (string Name, string? Value)[] entries)
    {
        return entries.ToDictionary(static entry => entry.Name, static entry => entry.Value, StringComparer.Ordinal);
    }

    private sealed class CountingProcessManager : CliProcessManager
    {
        public int ExecuteCount { get; private set; }

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class DiscoverySandbox : IDisposable
    {
        public DiscoverySandbox()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"discovery sandbox {Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string CreateDirectory(params string[] segments)
        {
            var path = Path.Combine([RootPath, .. segments]);
            Directory.CreateDirectory(path);
            return path;
        }

        public string WriteExecutable(string directory, string name, string body)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return Path.GetFullPath(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
