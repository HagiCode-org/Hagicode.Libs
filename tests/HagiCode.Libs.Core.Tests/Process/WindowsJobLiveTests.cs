using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Termination;
using HagiCode.Libs.Core.Process.Tree;
using HagiCode.Libs.Core.Process.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>
/// Real Windows Job Object containment: the Job itself, a descendant that outlives its exited parent, an orphan ended through
/// containment, a live platform read, and how many children escape the Job when they start right after launch.
/// These start real Windows processes, so they carry the WindowsOnly tier and are skipped everywhere else.
/// </summary>
[Category("Integration")]
[Category("WindowsOnly")]
public sealed class WindowsJobLiveTests
{
    private static string PowerShellPath => Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hagicode-job-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A child that still holds a file is ended by its Job; the leftover temp folder is harmless.
        }
    }

    [Test]
    public void A_real_job_assigns_a_child_lists_it_and_terminate_ends_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows Job Object test.");
        }

        using var child = StartSleep(60);
        try
        {
            using var job = WindowsJobFactory.Default.Create();

            var assigned = job.TryAssignProcess(child.Id, out var win32Error);
            assigned.ShouldBeTrue($"AssignProcessToJobObject failed with Win32 error {win32Error}.");
            job.ListMemberProcessIds().ShouldContain(child.Id);

            job.Terminate();

            child.WaitForExit(TimeSpan.FromSeconds(15)).ShouldBeTrue();
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [Test]
    public async Task A_descendant_outlives_its_exited_parent_and_is_gone_after_stop()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows Job Object test.");
        }

        var manager = CreateManager();
        var pidFile = Path.Combine(_root, "grandchild.pid");
        var handle = await manager.StartAsync(LaunchParentThatLeavesGrandchild(pidFile));
        var grandchildPid = 0;

        try
        {
            handle.IsContained.ShouldBeTrue();
            grandchildPid = await ReadPidAsync(pidFile);

            (await WaitUntilAsync(() => !handle.IsRunning, TimeSpan.FromSeconds(30))).ShouldBeTrue("The parent should exit first.");
            IsAlive(grandchildPid).ShouldBeTrue("The grandchild should outlive its exited parent.");
        }
        finally
        {
            // Stop disposes the handle, so it is called exactly once.
            await manager.StopAsync(handle);
        }

        (await WaitUntilAsync(() => !IsAlive(grandchildPid), TimeSpan.FromSeconds(15))).ShouldBeTrue("Stopping should end the grandchild through the Job.");
    }

    [Test]
    public async Task Terminating_a_contained_tree_after_its_root_exited_ends_the_orphaned_descendant_by_containment()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows Job Object test.");
        }

        var manager = CreateManager();
        var pidFile = Path.Combine(_root, "orphan.pid");
        var handle = await manager.StartAsync(LaunchParentThatLeavesGrandchild(pidFile));

        try
        {
            var grandchildPid = await ReadPidAsync(pidFile);
            (await WaitUntilAsync(() => !handle.IsRunning, TimeSpan.FromSeconds(30))).ShouldBeTrue();
            IsAlive(grandchildPid).ShouldBeTrue();

            var result = await manager.TerminateProcessTreeAsync(handle.Identity!.Value);

            result.Method.ShouldBe(CliProcessTerminationMethod.Containment);
            result.Outcome.ShouldNotBe(CliProcessTerminationOutcome.Failed);
            (await WaitUntilAsync(() => !IsAlive(grandchildPid), TimeSpan.FromSeconds(15))).ShouldBeTrue("The orphan should be ended by its Job.");
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task A_live_child_reads_every_snapshot_field_through_the_windows_platform()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows process read test.");
        }

        using var child = StartSleep(60);
        try
        {
            var platform = new WindowsProcessPlatform();

            var details = platform.ReadDetails(child.Id, includeCommandLine: true);

            details.ShouldNotBeNull();
            details!.Accessibility.ShouldBe(ProcessAccessibility.Accessible);
            details.StartedAtUtc.ShouldNotBeNull();
            var reported = new DateTimeOffset(child.StartTime);
            (details.StartedAtUtc!.Value - reported).Duration().ShouldBeLessThanOrEqualTo(new CliProcessOwnershipOptions().StartTimeTolerance);
            details.MemoryBytes.ShouldNotBeNull();
            details.MemoryBytes!.Value.ShouldBeGreaterThan(0L);
            details.CpuTime.ShouldNotBeNull();
            details.CommandLine.ShouldNotBeNull();
            details.CommandLine!.ShouldContain("Start-Sleep");

            // The name comes from the process table, not from ReadDetails.
            var rows = platform.ReadProcessTable();
            rows.Any(row => row.Pid == child.Id
                            && row.ParentPid == System.Environment.ProcessId
                            && (row.Name ?? string.Empty).Contains("powershell", StringComparison.OrdinalIgnoreCase))
                .ShouldBeTrue("The child should list the test process as its parent, with its executable name.");
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [Test]
    public async Task A_child_spawned_right_after_start_is_inside_the_job_and_none_escape()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows Job Object test.");
        }

        const int childCount = 8;
        var pidFile = Path.Combine(_root, "children.txt");
        var ledger = new CliManagedProcessLedger();
        var manager = CreateManager(ledger);
        var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = PowerShellPath,
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", SpawnChildrenImmediately(pidFile, childCount)],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = "job-escape-measure" },
        });

        try
        {
            // Each child is a separate PowerShell process, so this takes seconds on an idle host and longer in a loaded full run.
            (await WaitUntilAsync(() => File.Exists(pidFile) && File.ReadAllLines(pidFile).Length >= childCount, TimeSpan.FromSeconds(180)))
                .ShouldBeTrue("All children should start.");
            var childPids = File.ReadAllLines(pidFile).Select(line => int.Parse(line, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

            // Job membership is read from the Job itself. ProcessMembership only says how a process was found, so a Job member that the
            // tree walk also found is still labelled Descendant.
            ledger.TryGet(handle.Identity!.Value, out var entry).ShouldBeTrue("The launched root should be in the ledger.");
            var jobMembers = entry.Containment!.ListMemberProcessIds();
            var contained = childPids.Count(pid => jobMembers.Contains(pid));
            var escaped = childPids.Count(pid => !jobMembers.Contains(pid) && IsAlive(pid));

            TestContext.Out.WriteLine($"escape-measure children={childPids.Length} inJob={contained} escaped={escaped}");

            childPids.Length.ShouldBe(childCount);
            escaped.ShouldBe(0, "A child that starts right after the launch must still be assigned to the Job.");
            contained.ShouldBe(childCount);
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    private CliProcessManager CreateManager(CliManagedProcessLedger? ledger = null) =>
        new(
            new CliExecutableResolver(),
            new CliOwnedProcessRegistry(),
            new CliProcessOwnershipOptions(),
            TimeProvider.System,
            NullLogger<CliProcessManager>.Instance,
            managedProcessLedger: ledger);

    private ProcessStartContext LaunchParentThatLeavesGrandchild(string pidFile) => new()
    {
        ExecutablePath = PowerShellPath,
        Arguments =
        [
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            $"{StartDescendantScript} Set-Content -LiteralPath '{pidFile}' -Value $p.Id",
        ],
        Ownership = new CliProcessOwnershipRegistration { ProviderName = "job-live" },
    };

    // Spawns through Process.Start with UseShellExecute=false, which is how the library launches agents. Start-Process defaults to
    // ShellExecute, which is not the CreateProcess path a child inherits its parent's Job through.
    private static string StartDescendantScript =>
        $"$psi = New-Object System.Diagnostics.ProcessStartInfo; $psi.FileName = '{PowerShellPath}'; $psi.Arguments = '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 120'; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $p = [System.Diagnostics.Process]::Start($psi);";

    private static string SpawnChildrenImmediately(string pidFile, int count) =>
        $"1..{count} | ForEach-Object {{ $psi = New-Object System.Diagnostics.ProcessStartInfo; $psi.FileName = '{PowerShellPath}'; $psi.Arguments = '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 90'; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $p = [System.Diagnostics.Process]::Start($psi); Add-Content -LiteralPath '{pidFile}' -Value $p.Id }}; Start-Sleep -Seconds 90";

    private static System.Diagnostics.Process StartSleep(int seconds)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(PowerShellPath) { UseShellExecute = false, CreateNoWindow = true },
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add($"Start-Sleep -Seconds {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        process.Start();
        return process;
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        (await WaitUntilAsync(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0, TimeSpan.FromSeconds(30)))
            .ShouldBeTrue("The grandchild should write its pid.");
        return int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
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
