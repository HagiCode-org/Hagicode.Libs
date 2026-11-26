using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Platform;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>
/// Reads a real child through the Linux platform and checks its start time against the one the runtime reports.
/// Spawns a Linux child, so it carries the Integration tier.
/// </summary>
[Category("Integration")]
public sealed class LinuxProcessPlatformLiveTests
{
    [Test]
    public void A_live_child_start_time_matches_the_runtime_within_the_start_time_tolerance()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Reads /proc.");
        }

        using var child = StartSleep(60);
        try
        {
            var details = new LinuxProcessPlatform().ReadDetails(child.Id, includeCommandLine: false);

            details.ShouldNotBeNull();
            details!.StartedAtUtc.ShouldNotBeNull();
            var reported = new DateTimeOffset(child.StartTime);
            var drift = (details.StartedAtUtc!.Value - reported).Duration();
            drift.ShouldBeLessThanOrEqualTo(new CliProcessOwnershipOptions().StartTimeTolerance);
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [Test]
    public async Task A_real_transient_scope_child_expands_to_its_scope_members()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses systemd-run and /proc.");
        }

        var capability = CgroupScopeCapabilityProbe.Default.GetCapability();
        if (!capability.Supported)
        {
            Assert.Ignore($"systemd-run user scopes are not available on this host: {capability.Reason}.");
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo("systemd-run") { UseShellExecute = false };
        foreach (var argument in new[] { "--user", "--scope", "--quiet", "/bin/sleep", "60" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var launched = System.Diagnostics.Process.Start(startInfo)!;
        try
        {
            // systemd-run execs the command inside the scope, so the started pid is the sleep itself
            // (the same behavior CliProcessManagerResourceLimitTests checks for a raw systemd-run scope).
            var platform = new LinuxProcessPlatform();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!IsInTransientScope(launched.Id) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            IsInTransientScope(launched.Id).ShouldBeTrue();
            platform.ReadContainmentMembers(launched.Id).ShouldContain(launched.Id);
        }
        finally
        {
            KillQuietly(launched);
        }
    }

    [Test]
    public async Task A_root_that_exits_leaves_its_scope_workers_listed_as_residual()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses systemd-run and /proc.");
        }

        var capability = CgroupScopeCapabilityProbe.Default.GetCapability();
        if (!capability.Supported)
        {
            Assert.Ignore($"systemd-run user scopes are not available on this host: {capability.Reason}.");
        }

        // The shell is the scope's root; the background sleep is a worker that outlives it, like an idle build node.
        var startInfo = new System.Diagnostics.ProcessStartInfo("systemd-run") { UseShellExecute = false };
        foreach (var argument in new[] { "--user", "--scope", "--quiet", "/bin/sh", "-c", "sleep 60 & wait" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var launched = System.Diagnostics.Process.Start(startInfo)!;
        var platform = new LinuxProcessPlatform();
        var rootIdentity = new ProcessIdentity(launched.Id, new DateTimeOffset(launched.StartTime.ToUniversalTime(), TimeSpan.Zero));
        var ledger = new CliManagedProcessLedger(SystemProcessLivenessReader.Instance, platform, Timeout.InfiniteTimeSpan);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while ((!IsInTransientScope(launched.Id) || platform.ReadContainmentMembers(launched.Id).Count < 2) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            ledger.Add(
                new CliManagedProcessInfo(rootIdentity, "test", "session-live", "/bin/sh", null, CliManagedProcessOrigin.Started, IsContained: false),
                containment: null);
            ledger.GetAlive().Select(info => info.Pid).ShouldContain(launched.Id);

            launched.Kill();
            await launched.WaitForExitAsync();

            ledger.GetAlive().ShouldBeEmpty();
            ledger.GetResidual().Select(info => info.Pid).ShouldBe(new[] { launched.Id });
            var workers = ledger.GetResidualMembers(rootIdentity);
            workers.ShouldNotBeEmpty();
            workers.ShouldNotContain(launched.Id);

            foreach (var worker in workers)
            {
                platform.Kill(worker);
            }

            var emptied = DateTimeOffset.UtcNow.AddSeconds(10);
            while (ledger.GetResidual().Count > 0 && DateTimeOffset.UtcNow < emptied)
            {
                await Task.Delay(100);
            }

            ledger.GetResidual().ShouldBeEmpty();
        }
        finally
        {
            KillQuietly(launched);
            foreach (var worker in ledger.GetResidualMembers(rootIdentity))
            {
                platform.Kill(worker);
            }
        }
    }

    private static bool IsInTransientScope(int pid)
    {
        var cgroup = LinuxCgroupMembership.ParseUnifiedPath(File.ReadAllText(Path.Combine("/proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "cgroup")));
        var leaf = cgroup?[(cgroup.LastIndexOf('/') + 1)..];
        return leaf is not null && leaf.StartsWith("run-", StringComparison.Ordinal) && leaf.EndsWith(".scope", StringComparison.Ordinal);
    }

    private static System.Diagnostics.Process StartSleep(int seconds)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/sleep") { UseShellExecute = false },
        };
        process.StartInfo.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.Start();
        return process;
    }

    private static void KillQuietly(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
