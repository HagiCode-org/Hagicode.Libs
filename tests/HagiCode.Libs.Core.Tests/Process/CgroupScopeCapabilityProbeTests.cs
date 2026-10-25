using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CgroupScopeCapabilityProbeTests
{
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
                return true;
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
                return true;
            });

        var capability = probe.GetCapability();

        capability.Supported.ShouldBeFalse();
        capability.ReasonCode.ShouldBe("not-linux");
        capability.SystemdRunPath.ShouldBeNull();
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
                return true;
            });

        var capability = probe.GetCapability();

        capability.Supported.ShouldBeFalse();
        capability.ReasonCode.ShouldBe("systemd-run-missing");
        trials.ShouldBe(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void A_failed_or_throwing_trial_reports_user_scope_unavailable(bool throws)
    {
        var probe = new CgroupScopeCapabilityProbe(
            static () => true,
            static () => "/usr/bin/systemd-run",
            (_, _, _) => throws ? throw new InvalidOperationException("no user bus") : false);

        var capability = probe.GetCapability();

        capability.Supported.ShouldBeFalse();
        capability.Reason.ShouldBe(CgroupScopeCapabilityReason.UserScopeUnavailable);
        capability.ReasonCode.ShouldBe("user-scope-unavailable");
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
                return true;
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
            .ShouldBe(["ok", "not-linux", "systemd-run-missing", "user-scope-unavailable"]);
    }

    [Test]
    public void The_default_probe_is_one_shared_instance()
    {
        CgroupScopeCapabilityProbe.Default.ShouldBeSameAs(CgroupScopeCapabilityProbe.Default);
    }
}
