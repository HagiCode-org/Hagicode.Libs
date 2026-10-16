using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.CiSetup.Console;
using Shouldly;

namespace HagiCode.Libs.CiSetup.Tests;

public sealed class CiSetupRunnerTests
{
    private static readonly IReadOnlyDictionary<string, string?> HostEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["PATH"] = "/usr/bin:/home/u/.local/bin:/opt/tools",
        ["HOME"] = "/home/u"
    };

    [Test]
    public async Task Default_install_runs_npm_for_every_publicly_installable_descriptor()
    {
        var harness = new Harness(CliPlatform.Linux);

        var exitCode = await harness.Run(["--install"]);

        exitCode.ShouldBe(0);
        harness.Commands.Select(static c => c.Arguments).ShouldBe(
            CliInstallRegistry.PubliclyInstallable.Select(static d => (IReadOnlyList<string>)
                ["install", "--global", d.FullPackageSpecifier]).ToArray());
        harness.Commands.ShouldAllBe(static c => c.FileName == "/usr/bin/npm");
        harness.Commands.ShouldAllBe(static c => c.EnvironmentVariables.Count == 0);
        harness.Out.ToString().ShouldContain("Installing publicly available CLI tools...");
    }

    [Test]
    public async Task Default_install_fails_when_npm_is_missing()
    {
        var harness = new Harness(CliPlatform.Linux, npm: null);

        var exitCode = await harness.Run(["--install"]);

        exitCode.ShouldBe(1);
        harness.Err.ToString().ShouldContain("npm was not found on PATH");
        harness.Commands.ShouldBeEmpty();
    }

    [Test]
    public async Task Usage_is_printed_and_exits_one_when_no_mode_is_selected()
    {
        var harness = new Harness(CliPlatform.Linux);

        var exitCode = await harness.Run([]);

        exitCode.ShouldBe(1);
        harness.Err.ToString().ShouldContain("Usage: CiSetup.Console [--install] [--verify]");
    }

    [Test]
    public async Task Dry_run_argument_construction_for_claude_npm_pins_the_version_without_running_it()
    {
        var harness = new Harness(CliPlatform.Linux, installExitCode: 0);

        var exitCode = await harness.Run(["--install", "--provider", "ClaudeCode", "--strategy", "npm"]);

        exitCode.ShouldBe(0);
        var command = harness.Commands.Single();
        command.FileName.ShouldBe("/usr/bin/npm");
        command.Arguments.ShouldBe(["install", "--global", "@anthropic-ai/claude-code@2.1.168"]);
        command.EnvironmentVariables["DISABLE_AUTOUPDATER"].ShouldBe("1");
    }

    [Test]
    public async Task Provider_without_strategy_uses_the_npm_default()
    {
        var harness = new Harness(CliPlatform.Linux);

        var exitCode = await harness.Run(["--install", "--provider", "Copilot"]);

        exitCode.ShouldBe(0);
        harness.Commands.Single().Arguments.ShouldBe(["install", "--global", "@github/copilot@1.0.60"]);
    }

    [Test]
    public async Task Installer_failure_logs_provider_strategy_and_error_output_and_skips_verification()
    {
        var harness = new Harness(CliPlatform.Linux, installExitCode: 7, installStandardError: "E404 version not published");

        var exitCode = await harness.Run(["--install", "--provider", "ClaudeCode", "--strategy", "npm", "--verify-discovery"]);

        exitCode.ShouldBe(1);
        var error = harness.Err.ToString();
        error.ShouldContain("ClaudeCode");
        error.ShouldContain("npm");
        error.ShouldContain("E404 version not published");
        harness.DiscoveryCalls.ShouldBeEmpty();
    }

    [Test]
    public async Task Inapplicable_platform_fails_and_names_provider_strategy_and_platform()
    {
        var harness = new Harness(CliPlatform.Linux);

        var exitCode = await harness.Run(["--install", "--provider", "ClaudeCode", "--strategy", "winget"]);

        exitCode.ShouldBe(1);
        var error = harness.Err.ToString();
        error.ShouldContain("ClaudeCode");
        error.ShouldContain("winget");
        error.ShouldContain("Linux");
        harness.Commands.ShouldBeEmpty();
    }

    [Test]
    public async Task Native_install_passes_the_pinned_version_to_the_installer()
    {
        var harness = new Harness(CliPlatform.Linux);

        var exitCode = await harness.Run(["--install", "--provider", "ClaudeCode", "--strategy", "native"]);

        exitCode.ShouldBe(0);
        harness.Commands.Single().Arguments[^1].ShouldEndWith("bash -s 2.1.168");
    }

    [Test]
    public async Task Verify_discovery_passes_for_a_well_known_native_install()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: NativeFound());

        var exitCode = await harness.Run(
            ["--verify-discovery", "--provider", "ClaudeCode", "--strategy", "native", "--without-install-dir-on-path"]);

        exitCode.ShouldBe(0);
        harness.Out.ToString().ShouldContain("source: WellKnownLocation");
        harness.Out.ToString().ShouldContain("install kind: NativeInstaller");
        harness.Out.ToString().ShouldContain("version: 2.1.168");
    }

    [Test]
    public async Task Verify_discovery_removes_the_strategy_directory_from_path_when_asked()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: NativeFound());

        await harness.Run(
            ["--verify-discovery", "--provider", "ClaudeCode", "--strategy", "native", "--without-install-dir-on-path"]);

        var path = harness.DiscoveryCalls.Single().EnvironmentVariables!["PATH"];
        path.ShouldBe("/usr/bin:/opt/tools");
    }

    [Test]
    public async Task Verify_discovery_keeps_the_path_when_the_flag_is_absent()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: NativeFound());

        await harness.Run(["--verify-discovery", "--provider", "ClaudeCode", "--strategy", "native"]);

        harness.DiscoveryCalls.Single().EnvironmentVariables!["PATH"].ShouldBe(HostEnvironment["PATH"]);
    }

    [Test]
    public async Task Verify_discovery_off_path_fails_when_the_cli_resolves_through_path()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: NativeFound(CliDiscoverySource.Path));

        var exitCode = await harness.Run(
            ["--verify-discovery", "--provider", "ClaudeCode", "--strategy", "native", "--without-install-dir-on-path"]);

        exitCode.ShouldBe(3);
        harness.Err.ToString().ShouldContain("not found");
    }

    [Test]
    public async Task Verify_discovery_reports_not_found_with_exit_three_and_probed_locations()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: new CliDiscoveryResult("ClaudeCode")
        {
            ProbedLocations = ["/usr/bin/claude"]
        });

        var exitCode = await harness.Run(["--verify-discovery", "--provider", "ClaudeCode"]);

        exitCode.ShouldBe(3);
        var error = harness.Err.ToString();
        error.ShouldContain("not found");
        error.ShouldContain("/usr/bin/claude");
    }

    [Test]
    public async Task Verify_discovery_reports_version_unavailable_with_exit_four()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: new CliDiscoveryResult("ClaudeCode")
        {
            ResolvedPath = "/home/u/.local/bin/claude",
            Source = CliDiscoverySource.WellKnownLocation,
            InstallKind = CliInstallKind.NativeInstaller,
            VersionFailure = CliVersionProbeFailure.CommandFailed
        });

        var exitCode = await harness.Run(["--verify-discovery", "--provider", "ClaudeCode", "--strategy", "native"]);

        exitCode.ShouldBe(4);
        harness.Err.ToString().ShouldContain("version unavailable");
    }

    [Test]
    public async Task Verify_discovery_reports_version_mismatch_with_exit_five()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: NativeFound());

        var exitCode = await harness.Run(
            ["--verify-discovery", "--provider", "ClaudeCode", "--strategy", "native", "--expect-version", "9.9.9"]);

        exitCode.ShouldBe(5);
        harness.Err.ToString().ShouldContain("version mismatch: expected 9.9.9, observed 2.1.168");
    }

    [Test]
    public async Task Verify_discovery_reports_install_kind_mismatch_with_exit_six()
    {
        var harness = new Harness(CliPlatform.Linux, discovery: NativeFound(installKind: CliInstallKind.Npm));

        var exitCode = await harness.Run(
            ["--verify-discovery", "--provider", "ClaudeCode", "--strategy", "native", "--expect-install-kind", "native"]);

        exitCode.ShouldBe(6);
        harness.Err.ToString().ShouldContain("install kind mismatch: expected NativeInstaller, observed Npm");
    }

    [Test]
    public async Task Verify_discovery_without_provider_skips_non_public_descriptors()
    {
        var harness = new Harness(CliPlatform.Linux, discoveryFor: static descriptor => new CliDiscoveryResult(descriptor.ProviderName)
        {
            ResolvedPath = "/usr/bin/" + descriptor.ExecutableCandidates[0],
            Source = CliDiscoverySource.Path,
            InstallKind = CliInstallKind.Unknown,
            Version = descriptor.PinnedVersion.Length > 0 ? descriptor.PinnedVersion : "1.0.0",
            VersionFailure = CliVersionProbeFailure.None
        });

        var exitCode = await harness.Run(["--verify-discovery"]);

        exitCode.ShouldBe(0);
        harness.Out.ToString().ShouldContain("[SKIP] DeepAgents");
        harness.DiscoveryCalls.Select(static call => call.Descriptor.ProviderName)
            .ShouldBe(CliInstallRegistry.PubliclyInstallable.Select(static d => d.ProviderName).ToArray());
    }

    [Test]
    public async Task Off_path_option_without_strategy_is_a_usage_error()
    {
        var harness = new Harness(CliPlatform.Linux);

        var exitCode = await harness.Run(["--verify-discovery", "--provider", "ClaudeCode", "--without-install-dir-on-path"]);

        exitCode.ShouldBe(1);
        harness.Err.ToString().ShouldContain("--without-install-dir-on-path requires --strategy");
    }

    [Test]
    public async Task Strategy_without_provider_is_a_usage_error()
    {
        var harness = new Harness(CliPlatform.Linux);

        var exitCode = await harness.Run(["--verify-discovery", "--strategy", "npm"]);

        exitCode.ShouldBe(1);
        harness.Err.ToString().ShouldContain("require --provider");
    }

    private static CliDiscoveryResult NativeFound(
        CliDiscoverySource source = CliDiscoverySource.WellKnownLocation,
        CliInstallKind installKind = CliInstallKind.NativeInstaller)
    {
        return new CliDiscoveryResult("ClaudeCode")
        {
            ResolvedPath = "/home/u/.local/bin/claude",
            Source = source,
            InstallKind = installKind,
            Version = "2.1.168",
            VersionFailure = CliVersionProbeFailure.None
        };
    }

    private sealed record DiscoveryCall(CliInstallDescriptor Descriptor, IReadOnlyDictionary<string, string?>? EnvironmentVariables);

    private sealed class RecordingCommandRunner(int exitCode, string standardError) : ICiSetupCommandRunner
    {
        public List<CliInstallCommand> Commands { get; } = [];

        public Task<CiSetupCommandResult> RunAsync(CliInstallCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(new CiSetupCommandResult(exitCode, string.Empty, standardError));
        }
    }

    private sealed class Harness
    {
        private readonly RecordingCommandRunner _runner;
        private readonly CiSetupRunner _cli;

        public Harness(
            CliPlatform platform,
            string? npm = "/usr/bin/npm",
            int installExitCode = 0,
            string installStandardError = "",
            CliDiscoveryResult? discovery = null,
            Func<CliInstallDescriptor, CliDiscoveryResult>? discoveryFor = null)
        {
            _runner = new RecordingCommandRunner(installExitCode, installStandardError);
            Out = new StringWriter();
            Err = new StringWriter();
            var discoveryCalls = DiscoveryCalls;
            var environment = new CiSetupEnvironment(
                Out,
                Err,
                _runner,
                (descriptor, options, _) =>
                {
                    discoveryCalls.Add(new DiscoveryCall(descriptor, options.EnvironmentVariables));
                    var result = discoveryFor?.Invoke(descriptor)
                                 ?? discovery
                                 ?? new CliDiscoveryResult(descriptor.ProviderName);
                    return Task.FromResult(result);
                },
                platform,
                HostEnvironment,
                () => npm);
            _cli = new CiSetupRunner(environment);
        }

        public StringWriter Out { get; }

        public StringWriter Err { get; }

        public List<DiscoveryCall> DiscoveryCalls { get; } = [];

        public List<CliInstallCommand> Commands => _runner.Commands;

        public Task<int> Run(string[] args) => _cli.RunAsync(args);
    }
}
