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
