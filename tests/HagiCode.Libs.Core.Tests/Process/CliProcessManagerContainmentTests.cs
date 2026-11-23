using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Termination;
using HagiCode.Libs.Core.Process.Tree;
using HagiCode.Libs.Core.Process.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>
/// Windows Job containment control flow, run on Linux with a fake Job factory and a manager that reports Windows.
/// The fake Job ends its member processes on Terminate, the way a real Job would. Real Job behavior is verified by WindowsOnly tests on Windows.
/// These spawn Linux children, so they carry the Integration tier.
/// </summary>
[Category("Integration")]
public sealed class CliProcessManagerContainmentTests
{
    [Test]
    public async Task An_agent_launch_joins_a_job_after_creating_it_and_stopping_it_ends_the_members()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);
        var handle = await manager.StartAsync(SleepContext("contained-session"));

        handle.IsContained.ShouldBeTrue();
        factory.Events.ShouldBe(new[] { "create", "assign" });
        factory.Created.Single().AssignedPids.ShouldBe(new[] { handle.Pid });

        var job = factory.Created.Single();
        var identity = handle.Identity!.Value;
        await manager.StopAsync(handle);

        // StopAsync disposes the handle, so the process is checked by identity rather than through the disposed Process.
        job.TerminateCount.ShouldBeGreaterThanOrEqualTo(1);
        var snapshot = await ProcessTreeSnapshotter.Default.CaptureAsync(new ProcessTreeSnapshotRequest { Roots = [identity] });
        snapshot.Roots.Single().Status.ShouldBe(ProcessTreeRootStatus.NotFound);
        manager.GetAliveManagedProcesses().ShouldBeEmpty();
    }

    [Test]
    public async Task A_failed_assignment_falls_back_to_no_containment_and_the_launch_still_succeeds()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory { AssignSucceeds = false, AssignError = 5 };
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);

        var handle = await manager.StartAsync(SleepContext("fallback-session"));
        try
        {
            handle.IsContained.ShouldBeFalse();
            factory.Created.Single().DisposeCount.ShouldBe(1);
            handle.Process.HasExited.ShouldBeFalse();
            manager.GetAliveManagedProcesses().Single().IsContained.ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task A_factory_that_cannot_create_a_job_does_not_fail_the_launch()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory { CreateFailure = new System.ComponentModel.Win32Exception(5) };
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);

        var handle = await manager.StartAsync(SleepContext("create-failure-session"));
        try
        {
            handle.IsContained.ShouldBeFalse();
            handle.Process.HasExited.ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task The_kill_switch_keeps_the_job_out_of_the_launch()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions { WindowsJobContainment = false }, factory);

        var handle = await manager.StartAsync(SleepContext("switch-session"));
        try
        {
            factory.Created.ShouldBeEmpty();
            handle.IsContained.ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task A_launch_without_ownership_never_creates_a_job()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);

        var handle = await manager.StartAsync(new ProcessStartContext { ExecutablePath = "/bin/sleep", Arguments = ["60"] });
        try
        {
            factory.Created.ShouldBeEmpty();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task Disposing_a_contained_handle_closes_its_job_and_clears_the_ledger()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);
        var handle = await manager.StartAsync(SleepContext("dispose-session"));
        var pid = handle.Pid;

        // Disposing the handle closes the fake Job without ending the process, so the test ends it by pid afterwards.
        await handle.DisposeAsync();
        KillPid(pid);

        factory.Created.Single().DisposeCount.ShouldBe(1);
        factory.Created.Single().ReleaseCount.ShouldBe(0);
        manager.GetAliveManagedProcesses().ShouldBeEmpty();
    }

    [Test]
    public async Task An_external_registration_in_a_job_releases_it_without_ending_the_process()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);
        using var process = StartSleep(60);
        try
        {
            var registration = await manager.RegisterExternalProcessAsync(
                process,
                new CliExternalProcessRequest { ProviderName = "acp-provider", SessionId = "external-session" });

            registration.IsContained.ShouldBeTrue();
            factory.Created.Single().AssignedPids.ShouldBe(new[] { process.Id });

            await registration.DisposeAsync();

            factory.Created.Single().ReleaseCount.ShouldBe(1);
            factory.Created.Single().TerminateCount.ShouldBe(0);
            process.HasExited.ShouldBeFalse();
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Test]
    public async Task A_tree_request_for_a_contained_root_is_ended_by_its_job()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);
        var handle = await manager.StartAsync(SleepContext("job-tree-session"));
        try
        {
            var result = await manager.TerminateProcessTreeAsync(handle.Identity!.Value);

            result.Method.ShouldBe(CliProcessTerminationMethod.Containment);
            result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
            factory.Created.Single().TerminateCount.ShouldBe(1);
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task Off_Windows_a_launch_with_ownership_never_creates_a_job()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new CliProcessManager(
            new CliExecutableResolver(),
            new CliOwnedProcessRegistry(),
            new CliProcessOwnershipOptions(),
            TimeProvider.System,
            NullLogger<CliProcessManager>.Instance,
            jobFactory: factory);

        var handle = await manager.StartAsync(SleepContext("linux-session"));
        try
        {
            handle.IsContained.ShouldBeFalse();
            factory.Created.ShouldBeEmpty();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task A_subclass_override_of_the_public_virtuals_is_used_through_a_base_reference()
    {
        var overriding = new OverridingManager(new CliProcessOwnershipOptions(), new FakeWindowsJobFactory());
        CliProcessManager manager = overriding;

        manager.GetAliveManagedProcesses();
        await manager.TerminateProcessTreeAsync(new ProcessIdentity(int.MaxValue, DateTimeOffset.UnixEpoch));

        overriding.AliveCalls.ShouldBe(1);
        overriding.TreeTerminateCalls.ShouldBe(1);
    }

    private sealed class OverridingManager : CliProcessManager
    {
        public OverridingManager(CliProcessOwnershipOptions options, IWindowsJobFactory factory)
            : base(
                new CliExecutableResolver(),
                new CliOwnedProcessRegistry(),
                options,
                TimeProvider.System,
                NullLogger<CliProcessManager>.Instance,
                jobFactory: factory)
        {
        }

        public int AliveCalls { get; private set; }

        public int TreeTerminateCalls { get; private set; }

        public override IReadOnlyList<CliManagedProcessInfo> GetAliveManagedProcesses(CliManagedProcessQuery? query = null)
        {
            AliveCalls++;
            return base.GetAliveManagedProcesses(query);
        }

        public override Task<CliProcessTerminationResult> TerminateProcessTreeAsync(
            ProcessIdentity identity,
            CliProcessTerminationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            TreeTerminateCalls++;
            return base.TerminateProcessTreeAsync(identity, options, cancellationToken);
        }
    }

    [Test]
    public async Task A_failed_assignment_logs_a_warning_with_the_win32_error_and_the_start_still_succeeds()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var logger = new CapturingLogger<CliProcessManager>();
        var factory = new FakeWindowsJobFactory { AssignSucceeds = false, AssignError = 5 };
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory, logger);

        var handle = await manager.StartAsync(SleepContext("assign-warning-session"));
        try
        {
            logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("Windows Job Object", StringComparison.Ordinal)
                && entry.Message.Contains("Win32 error 5", StringComparison.Ordinal));
            handle.Process.HasExited.ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task A_failed_job_creation_logs_a_warning_with_the_win32_error_and_the_start_still_succeeds()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var logger = new CapturingLogger<CliProcessManager>();
        var factory = new FakeWindowsJobFactory { CreateFailure = new System.ComponentModel.Win32Exception(5) };
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory, logger);

        var handle = await manager.StartAsync(SleepContext("create-warning-session"));
        try
        {
            logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("Could not create a Windows Job Object", StringComparison.Ordinal)
                && entry.Message.Contains("Win32 error 5", StringComparison.Ordinal));
            handle.IsContained.ShouldBeFalse();
            handle.Process.HasExited.ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task Stopping_a_contained_process_ends_its_job_after_the_graceful_attempt()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);
        var handle = await manager.StartAsync(SleepContext("stop-order-session"));
        var identity = handle.Identity!.Value;

        await manager.StopAsync(handle);

        factory.Events.ToArray().ShouldBe(new[] { "create", "assign", "terminate" });
        factory.Created.Single().TerminateCount.ShouldBe(1);
        (await ProcessTreeSnapshotter.Default.CaptureAsync(new ProcessTreeSnapshotRequest { Roots = [identity] }))
            .Roots.Single().Status.ShouldBe(ProcessTreeRootStatus.NotFound);
    }

    [Test]
    public async Task The_job_is_still_terminated_when_the_root_exited_before_stop()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var factory = new FakeWindowsJobFactory();
        var manager = new WindowsPathManager(new CliProcessOwnershipOptions(), factory);
        var handle = await manager.StartAsync(SleepContext("root-exited-session"));

        handle.Process.Kill();
        handle.Process.WaitForExit(5000).ShouldBeTrue();
        await manager.StopAsync(handle);

        factory.Created.Single().TerminateCount.ShouldBe(1);
        factory.Events.ToArray().ShouldBe(new[] { "create", "assign", "terminate" });
    }

    private static ProcessStartContext SleepContext(string sessionId) => new()
    {
        ExecutablePath = "/bin/sleep",
        Arguments = ["60"],
        Ownership = new CliProcessOwnershipRegistration { ProviderName = "job-provider", SessionId = sessionId },
    };

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

    private static void KillPid(int pid)
    {
        try
        {
            System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// A manager that reports Windows, so the containment path runs on Linux with the fake Job factory.
    /// </summary>
    private sealed class WindowsPathManager : CliProcessManager
    {
        public WindowsPathManager(CliProcessOwnershipOptions options, IWindowsJobFactory factory, ILogger<CliProcessManager>? logger = null)
            : base(
                new CliExecutableResolver(),
                new CliOwnedProcessRegistry(),
                options,
                TimeProvider.System,
                logger ?? NullLogger<CliProcessManager>.Instance,
                jobFactory: factory)
        {
        }

        protected override bool IsWindows() => true;
    }

    private sealed class FakeWindowsJobFactory : IWindowsJobFactory
    {
        public List<string> Events { get; } = [];

        public List<FakeWindowsJob> Created { get; } = [];

        public Exception? CreateFailure { get; init; }

        public bool AssignSucceeds { get; init; } = true;

        public int AssignError { get; init; }

        public IWindowsProcessJob Create()
        {
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }

            Events.Add("create");
            var job = new FakeWindowsJob(this);
            Created.Add(job);
            return job;
        }

        public void RecordAssign() => Events.Add("assign");
    }

    private sealed class FakeWindowsJob(FakeWindowsJobFactory factory) : IWindowsProcessJob
    {
        public List<int> AssignedPids { get; } = [];

        public int TerminateCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool TryAssignProcess(int pid, out int win32Error)
        {
            if (!factory.AssignSucceeds)
            {
                win32Error = factory.AssignError;
                return false;
            }

            factory.RecordAssign();
            AssignedPids.Add(pid);
            win32Error = 0;
            return true;
        }

        public IReadOnlyList<int> ListMemberProcessIds() => AssignedPids.ToArray();

        public void Terminate()
        {
            TerminateCount++;
            factory.Events.Add("terminate");
            foreach (var pid in AssignedPids)
            {
                try
                {
                    System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        public void ReleaseKeepingMembers() => ReleaseCount++;

        public void Dispose() => DisposeCount++;
    }
}
