using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CliProcessManagerResourceLimitTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-memory-limit-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [Category("CriticalRegression")]
    public void The_parameterless_manager_builds_an_unwrapped_plan()
    {
        var plan = new CliProcessManager().CreateStartPlan(new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-c", "true"]
        });

        plan.StartInfo.FileName.ShouldBe("/bin/sh");
        plan.StartInfo.ArgumentList.ShouldBe(["-c", "true"]);
    }

    [Test]
    public void A_manager_without_a_provider_never_probes_the_host()
    {
        var trials = 0;
        var probe = new CgroupScopeCapabilityProbe(static () => true, static () => "/usr/bin/systemd-run", (_, _, _) => ++trials > 0);
        var manager = ResourceLimitTestSupport.CreateManager(limitProvider: null, probe);

        var plan = manager.CreateStartPlan(new ProcessStartContext { ExecutablePath = "/bin/sh", Arguments = ["-c", "true"] });

        plan.StartInfo.FileName.ShouldBe("/bin/sh");
        trials.ShouldBe(0);
    }

    [Test]
    public void A_wrapped_plan_names_systemd_run_while_the_resolved_path_stays_the_cli()
    {
        var manager = ResourceLimitTestSupport.CreateManager(new FixedResourceLimitProvider(4096), ResourceLimitTestSupport.SupportedProbe());

        var plan = manager.CreateStartPlan(new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-c", "printf '%s' \"$1\"", "sh", "two words"]
        });

        plan.ResolvedExecutablePath.ShouldBe("/bin/sh");
        plan.StartInfo.FileName.ShouldBe("/usr/bin/systemd-run");
        plan.StartInfo.ArgumentList.ShouldBe([.. new CliResourceLimits(4096).SystemdRunArguments, "/bin/sh", "-c", "printf '%s' \"$1\"", "sh", "two words"]);
        plan.StartInfo.Environment["NODE_OPTIONS"].ShouldNotBeNull().ShouldContain("--max-old-space-size=3072");
    }

    [Test]
    public void The_limit_is_read_for_every_plan_so_a_change_applies_to_later_launches_only()
    {
        var limit = 4096;
        var provider = new DelegateLimitProvider(() => new CliResourceLimits(limit));
        var manager = ResourceLimitTestSupport.CreateManager(provider, ResourceLimitTestSupport.SupportedProbe());
        var context = new ProcessStartContext { ExecutablePath = "/bin/sh", Arguments = ["-c", "true"] };

        var first = manager.CreateStartPlan(context);
        limit = 8192;
        var second = manager.CreateStartPlan(context);

        first.StartInfo.ArgumentList.ShouldContain("MemoryMax=4096M");
        second.StartInfo.ArgumentList.ShouldContain("MemoryMax=8192M");
    }

    [Test]
    public void A_context_that_opts_out_stays_unwrapped_and_does_not_read_the_limit()
    {
        var provider = new FixedResourceLimitProvider(4096);
        var manager = ResourceLimitTestSupport.CreateManager(provider, ResourceLimitTestSupport.SupportedProbe());

        var plan = manager.CreateStartPlan(new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["--version"],
            ApplyResourceLimits = false
        });

        plan.StartInfo.FileName.ShouldBe("/bin/sh");
        plan.StartInfo.ArgumentList.ShouldBe(["--version"]);
        provider.Calls.ShouldBe(0);
    }

    [Test]
    public void ProcessStartContext_applies_the_limit_by_default()
    {
        new ProcessStartContext { ExecutablePath = "cli" }.ApplyResourceLimits.ShouldBeTrue();
    }

    [Test]
    [Category("Integration")]
    public async Task A_wrapped_one_shot_result_names_the_cli_and_passes_output_and_exit_code_through()
    {
        RequireUnix();
        var systemdRun = ResourceLimitTestSupport.WriteFakeSystemdRun(_root);
        var manager = ResourceLimitTestSupport.CreateManager(new FixedResourceLimitProvider(4096), ResourceLimitTestSupport.SupportedProbe(systemdRun));
        var context = new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-c", "printf '%s|%s' \"$1\" \"$NODE_OPTIONS\"; exit 3", "sh", "two words"]
        };

        var result = await manager.ExecuteAsync(context);

        result.ExitCode.ShouldBe(3);
        result.StandardOutput.ShouldBe("two words|--max-old-space-size=3072");
        result.ResolvedExecutablePath.ShouldBe("/bin/sh");
        result.CommandPreview.ShouldBe(CommandPreviewFormatter.Format(context.ExecutablePath, context.Arguments));
        result.CommandPreview.ShouldNotContain("systemd-run");
    }

    [Test]
    [Category("Integration")]
    public async Task The_version_probe_opts_out_so_its_command_is_never_wrapped()
    {
        RequireUnix();
        // A systemd-run stand-in that always fails: a wrapped probe would find no version.
        Directory.CreateDirectory(_root);
        var failingSystemdRun = Path.Combine(_root, "systemd-run");
        File.WriteAllText(failingSystemdRun, "#!/bin/sh\nexit 97\n");
        File.SetUnixFileMode(failingSystemdRun, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cli = Path.Combine(_root, "fake-cli");
        File.WriteAllText(cli, "#!/bin/sh\necho 'fake-cli 1.2.3'\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var manager = ResourceLimitTestSupport.CreateManager(new FixedResourceLimitProvider(4096), ResourceLimitTestSupport.SupportedProbe(failingSystemdRun));
        var probe = new CliVersionProbe(manager);

        var version = await probe.TryGetVersionAsync(cli, [["--version"]]);

        version.ShouldBe("1.2.3");
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Uses a POSIX shell stand-in for systemd-run.");
        }
    }

    private sealed class DelegateLimitProvider(Func<CliResourceLimits?> getCurrent) : ICliResourceLimitProvider
    {
        public CliResourceLimits? GetCurrent() => getCurrent();
    }
}
