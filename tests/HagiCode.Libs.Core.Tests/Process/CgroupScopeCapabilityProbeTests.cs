using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CgroupScopeCapabilityProbeTests
{
    private static readonly string[] CpuTrialArguments =
    [
        "--user", "--scope", "--quiet", "-p", "CPUQuota=50%", "--",
        "/bin/sh", "-c", "cat \"/sys/fs/cgroup$(sed -n 's/^0:://p' /proc/self/cgroup)/cpu.max\" 2>/dev/null"
    ];

    [Test]
    public void A_successful_trial_scope_reports_ok_with_the_absolute_systemd_run_path()
    {
        string? trialPath = null;
        IReadOnlyList<string>? trialArguments = null;
        TimeSpan? trialTimeout = null;
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (path, arguments, timeout) =>
            {
                trialPath = path;
                trialArguments = arguments;
                trialTimeout = timeout;
                return new CgroupScopeTrialResult(0, string.Empty);
            });

        var capability = probe.GetCapability();

        capability.Supported.ShouldBeTrue();
        capability.Reason.ShouldBe(CgroupScopeCapabilityReason.Ok);
        capability.ReasonCode.ShouldBe("ok");
        capability.SystemdRunPath.ShouldBe("/usr/bin/systemd-run");
        trialPath.ShouldBe("/usr/bin/systemd-run");
        trialArguments.ShouldBe(["--user", "--scope", "--quiet", "-p", "MemoryMax=64M", "--", "true"]);
        trialTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void A_non_linux_host_reports_not_linux_and_spawns_nothing()
    {
        var lookups = 0;
        var trials = 0;
        var probe = new CgroupScopeCapabilityProbe(
            static () => false,
            () =>
            {
                lookups++;
                return "/usr/bin/systemd-run";
            },
            (_, _, _) =>
            {
                trials++;
                return new CgroupScopeTrialResult(0, string.Empty);
            });

        var capability = probe.GetCapability();
        var cpuCapability = probe.GetCpuCapability();

        capability.Supported.ShouldBeFalse();
        capability.ReasonCode.ShouldBe("not-linux");
        capability.SystemdRunPath.ShouldBeNull();
        cpuCapability.ReasonCode.ShouldBe("not-linux");
        lookups.ShouldBe(0);
        trials.ShouldBe(0);
    }

    [Test]
    public void A_missing_systemd_run_reports_systemd_run_missing_without_a_trial()
    {
        var trials = 0;
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => null,
            (_, _, _) =>
            {
                trials++;
                return new CgroupScopeTrialResult(0, string.Empty);
            });

        probe.GetCapability().ReasonCode.ShouldBe("systemd-run-missing");
        probe.GetCpuCapability().ReasonCode.ShouldBe("systemd-run-missing");
        trials.ShouldBe(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void A_failed_or_throwing_trial_reports_user_scope_unavailable(bool throws)
    {
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, _, _) => throws ? throw new InvalidOperationException("no user bus") : null);

        var capability = probe.GetCapability();

        capability.Supported.ShouldBeFalse();
        capability.Reason.ShouldBe(CgroupScopeCapabilityReason.UserScopeUnavailable);
        capability.ReasonCode.ShouldBe("user-scope-unavailable");
        probe.GetCpuCapability().ReasonCode.ShouldBe("user-scope-unavailable");
    }

    [Test]
    public void A_trial_that_exits_non_zero_reports_user_scope_unavailable()
    {
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, _, _) => new CgroupScopeTrialResult(1, string.Empty));

        probe.GetCapability().ReasonCode.ShouldBe("user-scope-unavailable");
    }

    [Test]
    public void The_memory_result_never_carries_the_cpu_reason()
    {
        // The memory trial succeeds while the CPU read-back says the controller is not delegated.
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, arguments, _) => new CgroupScopeTrialResult(0, arguments.Contains("CPUQuota=50%") ? "max 100000" : string.Empty));

        probe.GetCapability().Reason.ShouldBe(CgroupScopeCapabilityReason.Ok);
        probe.GetCpuCapability().Reason.ShouldBe(CgroupScopeCapabilityReason.CpuControllerUnavailable);
    }

    [Test]
    public void A_cpu_trial_reads_a_limited_quota_from_the_scope_and_reports_ok()
    {
        IReadOnlyList<string>? cpuArguments = null;
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, arguments, _) =>
            {
                if (arguments.Contains("CPUQuota=50%"))
                {
                    cpuArguments = arguments;
                    return new CgroupScopeTrialResult(0, "50000 100000\n");
                }

                return new CgroupScopeTrialResult(0, string.Empty);
            });

        var capability = probe.GetCpuCapability();

        capability.Supported.ShouldBeTrue();
        capability.ReasonCode.ShouldBe("ok");
        capability.SystemdRunPath.ShouldBe("/usr/bin/systemd-run");
        cpuArguments.ShouldBe(CpuTrialArguments);
    }

    [TestCase("max 100000\n")]
    [TestCase("")]
    [TestCase("   \n")]
    public void A_cpu_trial_that_exits_zero_without_a_limited_quota_reports_cpu_controller_unavailable(string cpuMax)
    {
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, arguments, _) => new CgroupScopeTrialResult(0, arguments.Contains("CPUQuota=50%") ? cpuMax : string.Empty));

        var capability = probe.GetCpuCapability();

        capability.Supported.ShouldBeFalse();
        capability.Reason.ShouldBe(CgroupScopeCapabilityReason.CpuControllerUnavailable);
        capability.ReasonCode.ShouldBe("cpu-controller-unavailable");
        probe.GetCapability().Reason.ShouldBe(CgroupScopeCapabilityReason.Ok);
    }

    [Test]
    public void A_cpu_trial_that_does_not_finish_reports_user_scope_unavailable_and_memory_is_decided_on_its_own()
    {
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, arguments, _) => arguments.Contains("CPUQuota=50%") ? null : new CgroupScopeTrialResult(0, string.Empty));

        probe.GetCpuCapability().ReasonCode.ShouldBe("user-scope-unavailable");
        probe.GetCapability().Supported.ShouldBeTrue();
    }

    [Test]
    public async Task The_cpu_result_is_cached_so_its_trial_runs_once()
    {
        var cpuTrials = 0;
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, arguments, _) =>
            {
                if (arguments.Contains("CPUQuota=50%"))
                {
                    Interlocked.Increment(ref cpuTrials);
                    return new CgroupScopeTrialResult(0, "50000 100000");
                }

                return new CgroupScopeTrialResult(0, string.Empty);
            });

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(probe.GetCpuCapability)));
        probe.GetCpuCapability();

        cpuTrials.ShouldBe(1);
    }

    [Test]
    public async Task The_result_is_cached_so_the_trial_runs_once()
    {
        var trials = 0;
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, _, _) =>
            {
                Interlocked.Increment(ref trials);
                return new CgroupScopeTrialResult(0, string.Empty);
            });

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(probe.GetCapability)));
        probe.GetCapability();

        trials.ShouldBe(1);
    }

    [Test]
    public void Reason_codes_match_the_settings_contract()
    {
        Enum.GetValues<CgroupScopeCapabilityReason>()
            .Select(CgroupScopeCapability.ToReasonCode)
            .ShouldBe(["ok", "not-linux", "systemd-run-missing", "user-scope-unavailable", "cpu-controller-unavailable"]);
    }

    [Test]
    public void The_default_probe_is_one_shared_instance()
    {
        CgroupScopeCapabilityProbe.Default.ShouldBeSameAs(CgroupScopeCapabilityProbe.Default);
    }
}
