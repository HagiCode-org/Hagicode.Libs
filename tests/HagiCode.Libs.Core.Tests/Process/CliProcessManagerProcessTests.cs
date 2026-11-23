using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Termination;
using HagiCode.Libs.Core.Process.Tree;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>
/// Real-process tests for the managed-process ledger, external registration, persisted recovery, and tree snapshots and termination.
/// These spawn Linux children, so they carry the Integration tier.
/// </summary>
[Category("Integration")]
public sealed class CliProcessManagerProcessTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
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
    public async Task A_started_process_with_ownership_is_listed_by_session_and_leaves_the_ledger_when_its_handle_is_disposed()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sleep",
            Arguments = ["60"],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = "test-provider", SessionId = "session-start" },
        });

        try
        {
            var listed = manager.GetAliveManagedProcesses(new CliManagedProcessQuery { SessionId = "session-start" });
            listed.Count.ShouldBe(1);
            listed[0].Pid.ShouldBe(handle.Pid);
            listed[0].Origin.ShouldBe(CliManagedProcessOrigin.Started);
            listed[0].ProviderName.ShouldBe("test-provider");
            listed[0].IsContained.ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }

        manager.GetAliveManagedProcesses().ShouldBeEmpty();
    }

    [Test]
    public async Task A_started_process_without_ownership_is_not_tracked()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        var handle = await manager.StartAsync(new ProcessStartContext { ExecutablePath = "/bin/sleep", Arguments = ["60"] });

        try
        {
            manager.GetAliveManagedProcesses().ShouldBeEmpty();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task With_persistence_off_a_process_with_an_unreadable_start_time_still_starts_and_is_not_tracked()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var logger = new CapturingLogger<CliProcessManager>();
        var manager = new UnreadableStartTimeManager(new CliProcessOwnershipOptions(), logger);
        var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sleep",
            Arguments = ["60"],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = "unreadable-provider" },
        });

        try
        {
            handle.IsRunning.ShouldBeTrue();
            manager.GetAliveManagedProcesses().ShouldBeEmpty();
            logger.Warnings.Any(message => message.Contains("start time cannot be read", StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task An_external_registration_is_listed_and_disposing_it_leaves_the_process_running()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        using var process = StartSleep(60);
        var manager = CreateManager(new CliProcessOwnershipOptions());
        try
        {
            var registration = await manager.RegisterExternalProcessAsync(
                process,
                new CliExternalProcessRequest { ProviderName = "acp-provider", SessionId = "session-ext" });

            var listed = manager.GetAliveManagedProcesses(new CliManagedProcessQuery { ProviderName = "acp-provider" });
            listed.Single().Origin.ShouldBe(CliManagedProcessOrigin.External);
            listed.Single().SessionId.ShouldBe("session-ext");
            registration.Identity.Pid.ShouldBe(process.Id);
            registration.IsContained.ShouldBeFalse();

            await registration.DisposeAsync();

            manager.GetAliveManagedProcesses().ShouldBeEmpty();
            process.HasExited.ShouldBeFalse();
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Test]
    public async Task A_registered_process_is_recovered_after_the_host_restarts_from_its_persisted_record()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var options = PersistedOptions();
        using var process = StartSleep(60);
        var registry = new CliOwnedProcessRegistry();

        // The first host registers the process and then goes away without disposing the registration.
        var firstHost = CreateManager(options);
        _ = await firstHost.RegisterExternalProcessAsync(
            process,
            new CliExternalProcessRequest { ProviderName = "acp-provider", SessionId = "session-recover" });

        var persisted = await registry.ReadAsync(options.StateFilePath!);
        persisted.Select(state => state.Pid).ShouldBe(new[] { process.Id });

        // A new host starts with an empty ledger and recovers from the file alone.
        var secondHost = CreateManager(options);
        var recovered = await secondHost.RecoverOwnedProcessesAsync();

        recovered.ShouldBe(1);
        process.WaitForExit(5000).ShouldBeTrue();
        (await registry.ReadAsync(options.StateFilePath!)).ShouldBeEmpty();
    }

    [Test]
    public async Task A_child_with_a_grandchild_is_seen_as_a_tree_with_depths_zero_one_and_two()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sh.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        var handle = await StartTreeAsync(manager, "tree-session");
        try
        {
            var tree = await WaitForTreeAsync(handle.Identity!.Value, hasDepthTwo: true);

            tree.Status.ShouldBe(ProcessTreeRootStatus.Found);
            tree.Processes.First().Depth.ShouldBe(0);
            tree.Processes.Select(entry => entry.Depth).Distinct().OrderBy(depth => depth).ToArray().ShouldBe(new[] { 0, 1, 2 });

            // Every entry's parent appears before it, so the list is parent-first.
            var seen = new HashSet<int>();
            foreach (var entry in tree.Processes)
            {
                if (entry.Membership != ProcessMembership.Root)
                {
                    seen.ShouldContain(entry.ParentPid);
                }

                seen.Add(entry.Pid);
            }
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task Terminating_a_tree_ends_the_grandchild_and_the_child_and_reports_leaves_first()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sh.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        var handle = await StartTreeAsync(manager, "terminate-session");
        var root = handle.Identity!.Value;
        try
        {
            var before = await WaitForTreeAsync(root, hasDepthTwo: true);

            var result = await manager.TerminateProcessTreeAsync(root);

            result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
            result.Method.ShouldBe(CliProcessTerminationMethod.TreeWalk);
            result.Items.Count.ShouldBe(before.Processes.Count);
            result.Items[^1].Identity.Pid.ShouldBe(root.Pid);
            result.Items[0].Identity.Pid.ShouldNotBe(root.Pid);

            var after = await ProcessTreeSnapshotter.Default.CaptureAsync(new ProcessTreeSnapshotRequest { Roots = [root] });
            after.Roots.Single().Status.ShouldBe(ProcessTreeRootStatus.NotFound);
            manager.GetAliveManagedProcesses().ShouldBeEmpty();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task A_fabricated_identity_is_rejected_and_the_unrelated_process_keeps_running()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        using var process = StartSleep(60);
        try
        {
            var real = new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
            var fabricated = real with { StartedAtUtc = real.StartedAtUtc.AddHours(1) };

            var result = await CreateManager(new CliProcessOwnershipOptions()).TerminateProcessTreeAsync(fabricated);

            result.Outcome.ShouldBe(CliProcessTerminationOutcome.IdentityMismatch);
            process.HasExited.ShouldBeFalse();
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Test]
    public async Task A_single_real_child_is_terminated_by_identity()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        using var process = StartSleep(60);
        try
        {
            var identity = new ProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));

            var result = await CreateManager(new CliProcessOwnershipOptions()).TerminateProcessAsync(identity);

            result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
            result.Method.ShouldBe(CliProcessTerminationMethod.Single);
            process.WaitForExit(5000).ShouldBeTrue();
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Test]
    public async Task Capturing_managed_trees_pairs_each_alive_managed_process_with_its_tree()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sh.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        var handle = await StartTreeAsync(manager, "capture-session");
        try
        {
            await WaitForTreeAsync(handle.Identity!.Value, hasDepthTwo: true);

            var trees = await manager.CaptureManagedProcessTreesAsync(new CliManagedProcessQuery { SessionId = "capture-session" });

            trees.Count.ShouldBe(1);
            trees[0].Process.Pid.ShouldBe(handle.Pid);
            trees[0].Process.Origin.ShouldBe(CliManagedProcessOrigin.Started);
            trees[0].Tree.Status.ShouldBe(ProcessTreeRootStatus.Found);
            trees[0].Tree.Processes.Max(entry => entry.Depth).ShouldBe(2);
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task A_handle_keeps_its_pid_and_identity_after_it_is_stopped()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sleep",
            Arguments = ["60"],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = "test-provider", SessionId = "identity-session" },
        });
        var pid = handle.Pid;
        var identity = handle.Identity;

        await manager.StopAsync(handle);

        handle.Pid.ShouldBe(pid);
        handle.StartedAtUtc.ShouldNotBeNull();
        handle.Identity.ShouldBe(identity);
    }

    [Test]
    public async Task A_stale_persisted_record_is_skipped_and_removed_while_the_unrelated_process_keeps_running()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var options = PersistedOptions();
        using var process = StartSleep(60);
        try
        {
            // The record names this pid with a start time from long ago, as if the pid had since been reused.
            var stale = new CliOwnedProcessState(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero).AddHours(-3),
                "/bin/sleep",
                null,
                "acp-provider",
                DateTimeOffset.UtcNow);
            await new CliOwnedProcessRegistry().AddOrUpdateAsync(options.StateFilePath!, stale);

            var recovered = await CreateManager(options).RecoverOwnedProcessesAsync();

            recovered.ShouldBe(0);
            process.HasExited.ShouldBeFalse();
            (await new CliOwnedProcessRegistry().ReadAsync(options.StateFilePath!)).ShouldBeEmpty();
        }
        finally
        {
            KillQuietly(process);
        }
    }

    // Mirrors the "Registering a process the caller started" snippet in README.md, with /bin/sleep standing in for the CLI.
    [Test]
    public async Task The_readme_external_registration_example_runs_as_written()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep in place of the CLI.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        var sessionId = "readme-session";
        var workingDirectory = _root;
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sleep") { UseShellExecute = false, ArgumentList = { "60" } })!;
        try
        {
            await using var registration = await manager.RegisterExternalProcessAsync(
                process,
                new CliExternalProcessRequest { ProviderName = "claude-code", SessionId = sessionId, WorkingDirectory = workingDirectory });

            // Later: find the session's processes and end one of them by identity.
            var alive = manager.GetAliveManagedProcesses(new CliManagedProcessQuery { SessionId = sessionId });
            var result = await manager.TerminateProcessTreeAsync(alive[0].Identity);

            result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Test]
    public async Task Registering_an_exited_process_fails_clearly_and_adds_nothing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/true.");
        }

        var manager = CreateManager(new CliProcessOwnershipOptions());
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/true") { UseShellExecute = false },
        };
        process.Start();
        process.WaitForExit();

        await Should.ThrowAsync<InvalidOperationException>(async () => await manager.RegisterExternalProcessAsync(
            process,
            new CliExternalProcessRequest { ProviderName = "external-provider", SessionId = "exited-session" }));

        manager.GetAliveManagedProcesses().ShouldBeEmpty();
    }

    [Test]
    public async Task With_persistence_off_a_started_process_is_tracked_and_no_state_file_is_created()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var stateFilePath = Path.Combine(_root, "not-created.json");
        var manager = CreateManager(new CliProcessOwnershipOptions { Enabled = false, StateFilePath = stateFilePath });
        var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sleep",
            Arguments = ["60"],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = "untracked-provider", SessionId = "session-memory" },
        });
        try
        {
            manager.GetAliveManagedProcesses(new CliManagedProcessQuery { SessionId = "session-memory" })
                .Single().Identity.Pid.ShouldBe(handle.Pid);
            File.Exists(stateFilePath).ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task With_persistence_on_the_state_file_keeps_its_fields_and_never_stores_the_session_id()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var options = PersistedOptions();
        var manager = CreateManager(options);
        var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sleep",
            Arguments = ["60"],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = "persist-provider", SessionId = "session-not-persisted" },
        });
        try
        {
            var records = await new CliOwnedProcessRegistry().ReadAsync(options.StateFilePath!);
            records.Single().Pid.ShouldBe(handle.Pid);
            records.Single().ProviderName.ShouldBe("persist-provider");
            (await File.ReadAllTextAsync(options.StateFilePath!)).ShouldNotContain("session-not-persisted");
            manager.GetAliveManagedProcesses(new CliManagedProcessQuery { SessionId = "session-not-persisted" }).ShouldHaveSingleItem();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Test]
    public async Task Disposing_an_external_registration_removes_its_persisted_record_and_leaves_the_process_running()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var options = PersistedOptions();
        using var process = StartSleep(60);
        var registry = new CliOwnedProcessRegistry();
        try
        {
            var manager = CreateManager(options);
            var registration = await manager.RegisterExternalProcessAsync(
                process,
                new CliExternalProcessRequest { ProviderName = "acp-provider", SessionId = "session-dispose" });
            (await registry.ReadAsync(options.StateFilePath!)).Count.ShouldBe(1);

            await registration.DisposeAsync();

            (await registry.ReadAsync(options.StateFilePath!)).ShouldBeEmpty();
            manager.GetAliveManagedProcesses().ShouldBeEmpty();
            process.HasExited.ShouldBeFalse();
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Test]
    public async Task An_access_denied_recovery_is_logged_and_the_other_records_are_still_processed_and_removed()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Uses /bin/sleep.");
        }

        var options = PersistedOptions();
        using var denied = StartSleep(60);
        using var other = StartSleep(60);
        try
        {
            var seeder = CreateManager(options);
            _ = await seeder.RegisterExternalProcessAsync(denied, new CliExternalProcessRequest { ProviderName = "acp-provider", SessionId = "denied" });
            _ = await seeder.RegisterExternalProcessAsync(other, new CliExternalProcessRequest { ProviderName = "acp-provider", SessionId = "other" });

            var logger = new CapturingLogger<CliProcessManager>();
            var recovering = new DeniedRecoveryManager(options, logger, deniedPid: denied.Id);

            var recovered = await recovering.RecoverOwnedProcessesAsync();

            recovered.ShouldBe(1);
            logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("AccessDenied", StringComparison.Ordinal));
            other.WaitForExit(5000).ShouldBeTrue();
            denied.HasExited.ShouldBeFalse();
            (await new CliOwnedProcessRegistry().ReadAsync(options.StateFilePath!)).ShouldBeEmpty();
        }
        finally
        {
            KillQuietly(denied);
            KillQuietly(other);
        }
    }

    /// <summary>
    /// Reports access denied for one pid during recovery, so the access-denied path runs on Linux.
    /// </summary>
    private sealed class DeniedRecoveryManager : CliProcessManager
    {
        private readonly int _deniedPid;

        public DeniedRecoveryManager(CliProcessOwnershipOptions options, ILogger<CliProcessManager> logger, int deniedPid)
            : base(
                new CliExecutableResolver(),
                new CliOwnedProcessRegistry(),
                options,
                TimeProvider.System,
                logger)
        {
            _deniedPid = deniedPid;
        }

        public override Task<CliProcessTerminationResult> TerminateProcessTreeAsync(
            ProcessIdentity identity,
            CliProcessTerminationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (identity.Pid == _deniedPid)
            {
                return Task.FromResult(new CliProcessTerminationResult(
                    identity,
                    CliProcessTerminationOutcome.AccessDenied,
                    CliProcessTerminationMethod.TreeWalk,
                    [],
                    "Access to the process was denied."));
            }

            return base.TerminateProcessTreeAsync(identity, options, cancellationToken);
        }
    }

    private sealed class UnreadableStartTimeManager : CliProcessManager
    {
        public UnreadableStartTimeManager(CliProcessOwnershipOptions options, ILogger<CliProcessManager> logger)
            : base(
                new CliExecutableResolver(),
                new CliOwnedProcessRegistry(),
                options,
                TimeProvider.System,
                logger)
        {
        }

        internal override bool TryGetProcessStartTimeUtc(System.Diagnostics.Process process, out DateTimeOffset startedAtUtc)
        {
            startedAtUtc = default;
            return false;
        }
    }

    private CliProcessManager CreateManager(CliProcessOwnershipOptions options) =>
        new(
            new CliExecutableResolver(),
            new CliOwnedProcessRegistry(),
            options,
            TimeProvider.System,
            NullLogger<CliProcessManager>.Instance);

    private CliProcessOwnershipOptions PersistedOptions() => new()
    {
        Enabled = true,
        StateFilePath = Path.Combine(_root, "owned-processes.json"),
    };

    private static async Task<CliProcessHandle> StartTreeAsync(CliProcessManager manager, string sessionId) =>
        await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-c", "sh -c 'sleep 60; true' & sleep 60"],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = "tree-provider", SessionId = sessionId },
        });

    // The grandchild is started by the background shell, so it can appear a moment after the root.
    private static async Task<ProcessTreeRootSnapshot> WaitForTreeAsync(ProcessIdentity root, bool hasDepthTwo)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var snapshot = await ProcessTreeSnapshotter.Default.CaptureAsync(new ProcessTreeSnapshotRequest { Roots = [root] });
            var tree = snapshot.Roots.Single();
            var ready = !hasDepthTwo || tree.Processes.Any(entry => entry.Depth == 2);
            if ((tree.Status == ProcessTreeRootStatus.Found && ready) || DateTimeOffset.UtcNow >= deadline)
            {
                return tree;
            }

            await Task.Delay(50);
        }
    }

    private static System.Diagnostics.Process StartSleep(int seconds)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/sleep")
            {
                UseShellExecute = false,
            },
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
