using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliVersionProbeTests
{
    private const string JpackageLauncherVariable = "_JPACKAGE_LAUNCHER";

    [Test]
    public void GetCopilotVersionArgumentSets_returns_version_then_double_dash_version_then_v()
    {
        var sets = CliVersionProbe.GetCopilotVersionArgumentSets();

        sets.Count.ShouldBe(3);
        sets[0].ShouldBe(["version"]);
        sets[1].ShouldBe(["--version"]);
        sets[2].ShouldBe(["-v"]);
    }

    [Test]
    public async Task ProbeVersionAsync_rejects_null_or_whitespace_executable_path()
    {
        var probe = new CliVersionProbe(new StubProcessManager(_ => throw new InvalidOperationException("must not run")));

        await Should.ThrowAsync<ArgumentException>(() => probe.ProbeVersionAsync(null!, [["--version"]]));
        await Should.ThrowAsync<ArgumentException>(() => probe.ProbeVersionAsync("   ", [["--version"]]));
    }

    [Test]
    public async Task ProbeVersionAsync_rejects_null_argument_sets()
    {
        var probe = new CliVersionProbe(new StubProcessManager(_ => throw new InvalidOperationException("must not run")));

        await Should.ThrowAsync<ArgumentNullException>(() => probe.ProbeVersionAsync("copilot", null!));
    }

    [Test]
    public async Task ProbeVersionAsync_with_empty_argument_sets_returns_no_version_without_starting_a_process()
    {
        var stub = new StubProcessManager(_ => throw new InvalidOperationException("must not run"));
        var probe = new CliVersionProbe(stub);

        var result = await probe.ProbeVersionAsync("copilot", []);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.InvalidOutput);
        stub.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task TryGetVersionAsync_with_candidates_skips_blank_entries_and_probes_duplicates_once()
    {
        var stub = new StubProcessManager(_ => new ProcessResult(1, string.Empty, string.Empty));
        var probe = new CliVersionProbe(stub);
        string?[] candidates = ["", "   ", null, "/opt/cli/a", "/opt/cli/a", "/opt/cli/b"];

        var version = await probe.TryGetVersionAsync(candidates, [["--version"]]);

        version.ShouldBeNull();
        stub.Calls.Select(static call => call.ExecutablePath).ShouldBe(["/opt/cli/a", "/opt/cli/b"]);
    }

    [Test]
    public async Task TryGetVersionAsync_with_null_candidates_throws_argument_null()
    {
        var probe = new CliVersionProbe(new StubProcessManager(_ => throw new InvalidOperationException("must not run")));

        await Should.ThrowAsync<ArgumentNullException>(() => probe.TryGetVersionAsync((IEnumerable<string?>)null!, [["--version"]]));
    }

    [Test]
    public async Task TryGetVersionAsync_with_candidates_falls_through_a_failing_candidate_to_the_next_version()
    {
        using var fixture = CreateExecutable("echo 'Copilot CLI 1.0.88'");
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing cli {Guid.NewGuid():N}", "copilot");
        var probe = new CliVersionProbe();

        var version = await probe.TryGetVersionAsync(new[] { missingPath, fixture.Path }, [["--version"]]);

        version.ShouldBe("1.0.88");
    }

    [Test]
    public async Task ProbeVersionAsync_extracts_product_prefixed_junie_version()
    {
        using var fixture = CreateExecutable("echo 'Junie version: 26.9.22 (3419.22)'");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBe("26.9.22");
        result.Failure.ShouldBe(CliVersionProbeFailure.None);
    }

    [Test]
    public async Task TryGetVersionAsync_extracts_copilot_version_token_from_copilot_argument_sets()
    {
        using var fixture = CreateExecutable("echo 'GitHub Copilot CLI v1.0.88'");

        var version = await new CliVersionProbe().TryGetVersionAsync(
            fixture.Path,
            CliVersionProbe.GetCopilotVersionArgumentSets());

        version.ShouldBe("1.0.88");
    }

    [Test]
    public async Task ProbeVersionAsync_keeps_prerelease_and_build_metadata_without_the_v_prefix()
    {
        using var fixture = CreateExecutable("echo 'v1.0.60-beta.2+build.5'");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBe("1.0.60-beta.2+build.5");
    }

    [Test]
    public async Task ProbeVersionAsync_accepts_a_version_followed_by_a_sentence_period()
    {
        using var fixture = CreateExecutable("echo 'GitHub Copilot CLI 1.0.60.'");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBe("1.0.60");
    }

    [Test]
    public async Task ProbeVersionAsync_searches_standard_error_when_standard_output_has_no_version()
    {
        using var fixture = CreateExecutable("echo 'Checking CLI...' && echo 'GitHub Copilot CLI 1.0.88' >&2");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBe("1.0.88");
    }

    [Test]
    public async Task ProbeVersionAsync_tries_the_next_argument_set_after_a_command_failure()
    {
        using var fixture = CreateExecutable(
            "if [ \"$1\" = \"--version\" ]; then echo 'GitHub Copilot CLI 1.0.88'; else exit 2; fi");

        var result = await new CliVersionProbe().ProbeVersionAsync(
            fixture.Path,
            [["version"], ["--version"]]);

        result.Version.ShouldBe("1.0.88");
        result.Failure.ShouldBe(CliVersionProbeFailure.None);
    }

    [Test]
    public async Task ProbeVersionAsync_does_not_treat_arbitrary_output_as_a_version()
    {
        using var fixture = CreateExecutable("echo 'Copilot startup failed'");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.InvalidOutput);
    }

    [Test]
    public async Task ProbeVersionAsync_ignores_the_node_js_runtime_banner()
    {
        using var fixture = CreateExecutable("echo 'Node.js v22.8.0'; echo '2.1.168 (Claude Code)'");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBe("2.1.168");
    }

    [Test]
    public async Task ProbeVersionAsync_does_not_use_a_banner_only_version_as_the_cli_version()
    {
        using var fixture = CreateExecutable("echo 'Node.js v22.8.0'; echo 'npm 10.9.0'; echo 'Copilot startup failed' >&2");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.InvalidOutput);
    }

    [Test]
    public async Task ProbeVersionAsync_preserves_argument_list_boundaries()
    {
        using var fixture = CreateExecutable(
            "if [ \"$1\" = \"argument with spaces\" ]; then echo 'Copilot CLI 1.0.88'; else exit 2; fi");

        var result = await new CliVersionProbe().ProbeVersionAsync(
            fixture.Path,
            [["argument with spaces"]]);

        result.Version.ShouldBe("1.0.88");
    }

    [Test]
    public async Task ProbeVersionAsync_reports_command_failed_for_a_non_zero_exit()
    {
        using var fixture = CreateExecutable("exit 2");

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.CommandFailed);
    }

    [Test]
    public async Task ProbeVersionAsync_reports_process_start_failed_for_a_missing_executable()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing cli {Guid.NewGuid():N}", "copilot");

        var result = await new CliVersionProbe().ProbeVersionAsync(missingPath, [["--version"]]);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.ProcessStartFailed);
    }

    [Test]
    public async Task ProbeVersionAsync_reports_process_start_failed_for_a_non_executable_file()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("This test relies on POSIX execute permission.");
        }

        using var fixture = CreateExecutable("echo 'Copilot CLI 1.0.88'");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Failure.ShouldBe(CliVersionProbeFailure.ProcessStartFailed);
    }

    [Test]
    public async Task ProbeVersionAsync_reports_the_category_of_the_last_attempt()
    {
        using var fixture = CreateExecutable(
            "if [ \"$1\" = \"version\" ]; then exit 2; else echo 'Copilot startup failed'; fi");

        var result = await new CliVersionProbe().ProbeVersionAsync(
            fixture.Path,
            [["version"], ["--version"]]);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.InvalidOutput);
    }

    [Test]
    public async Task ProbeVersionAsync_reports_timed_out_and_stops_the_process()
    {
        using var fixture = CreateExecutable("sleep 30");
        var probe = new CliVersionProbe(new CliProcessManager(), TimeSpan.FromMilliseconds(300));

        var result = await probe.ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.TimedOut);
    }

    [Test]
    public async Task ProbeVersionAsync_propagates_caller_cancellation_instead_of_a_failure_category()
    {
        using var fixture = CreateExecutable("sleep 30");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]], cancellationToken: cancellation.Token));
    }

    [Test]
    public async Task ProbeVersionAsync_parses_only_the_retained_output_when_output_exceeds_the_cap()
    {
        // 40,000 characters on the first line exceed the 32 KiB retained cap; the version sits past the cap.
        using var fixture = CreateExecutable("head -c 40000 /dev/zero | tr '\\0' 'x'; echo; echo 'Copilot CLI 1.0.88'");

        var result = await new CliVersionProbe(new CliProcessManager(), TimeSpan.FromSeconds(30))
            .ProbeVersionAsync(fixture.Path, [["--version"]]);

        result.Version.ShouldBeNull();
        result.Failure.ShouldBe(CliVersionProbeFailure.InvalidOutput);
    }

    [Test]
    public async Task ProbeVersionAsync_removes_jpackage_launcher_from_the_child_environment()
    {
        var stub = new StubProcessManager(_ => new ProcessResult(0, "1.0.0", string.Empty));
        var probe = new CliVersionProbe(stub);

        await probe.ProbeVersionAsync("copilot", [["--version"]], new Dictionary<string, string?>
        {
            [JpackageLauncherVariable] = "/opt/launcher"
        });

        var environment = stub.Calls.Single().EnvironmentVariables!;
        environment.ContainsKey(JpackageLauncherVariable).ShouldBeTrue();
        environment[JpackageLauncherVariable].ShouldBeNull();
    }

    [Test]
    public async Task ProbeVersionAsync_does_not_leak_an_inherited_jpackage_launcher_variable()
    {
        using var fixture = CreateExecutable(
            "if [ -n \"$_JPACKAGE_LAUNCHER\" ]; then echo 'leaked 9.9.9'; else echo 'Copilot CLI 1.0.88'; fi");
        var originalValue = System.Environment.GetEnvironmentVariable(JpackageLauncherVariable);
        try
        {
            System.Environment.SetEnvironmentVariable(JpackageLauncherVariable, "/opt/launcher");

            var result = await new CliVersionProbe().ProbeVersionAsync(fixture.Path, [["--version"]]);

            result.Version.ShouldBe("1.0.88");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(JpackageLauncherVariable, originalValue);
        }
    }

    [Test]
    public async Task ProbeVersionAsync_passes_the_supplied_environment_to_the_child_and_null_removes_a_variable()
    {
        using var fixture = CreateExecutable(
            "if [ \"$PROBE_TEST_VALUE\" = \"from-probe\" ] && [ -z \"$PROBE_REMOVED_VALUE\" ]; then echo 'Copilot CLI 1.0.88'; else exit 2; fi");

        var result = await new CliVersionProbe().ProbeVersionAsync(
            fixture.Path,
            [["--version"]],
            new Dictionary<string, string?>
            {
                ["PROBE_TEST_VALUE"] = "from-probe",
                ["PROBE_REMOVED_VALUE"] = null
            });

        result.Version.ShouldBe("1.0.88");
    }

    [Test]
    [Category("WindowsOnly")]
    public async Task TryGetVersionAsync_runs_a_cmd_shim_under_a_path_with_spaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows-specific .cmd shim test.");
        }

        using var fixture = CreateWindowsShim();

        var version = await new CliVersionProbe().TryGetVersionAsync(fixture.Path, [["--version"]]);

        version.ShouldBe("1.0.88");
    }

    private static TestExecutable CreateExecutable(string scriptBody)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("This test uses a POSIX shell executable.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"cli probe with spaces {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "copilot");
        File.WriteAllText(path, $"#!/bin/sh\n{scriptBody}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return new TestExecutable(directory, path);
    }

    private static TestExecutable CreateWindowsShim()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cli probe with spaces {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "copilot.cmd");
        File.WriteAllText(path, "@echo off\r\necho GitHub Copilot CLI 1.0.88\r\n");
        return new TestExecutable(directory, path);
    }

    private sealed class StubProcessManager(Func<ProcessStartContext, ProcessResult> handler) : CliProcessManager
    {
        public List<ProcessStartContext> Calls { get; } = [];

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add(context);
            return Task.FromResult(handler(context));
        }
    }

    private sealed class TestExecutable(string directory, string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
