using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Tree;
using HagiCode.Libs.Core.Process.Windows;

namespace HagiCode.Libs.Core.Process.Termination;

/// <summary>
/// Ends one process or a process subtree after confirming that each process is still the one that was recorded.
/// </summary>
/// <remarks>
/// Termination never throws for the expected cases; it returns a typed outcome instead. The only exception that escapes is <see cref="OperationCanceledException" />, and only before work starts.
/// A process id can still be reused in the microseconds between the identity check and the kill. That residual gap is inherent to PID-based termination and is stated in the README.
/// </remarks>
public sealed class CliProcessTerminator
{
    private const int SubtreeCapacity = 4096;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Lazy<CliProcessTerminator> SharedDefault = new(() => new CliProcessTerminator());

    private readonly IProcessPlatform _platform;
    private readonly TimeSpan _startTimeTolerance;
    private readonly ProcessTreeSnapshotter _snapshotter;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliProcessTerminator" /> class for the running operating system, with a one-second start-time tolerance.
    /// </summary>
    public CliProcessTerminator()
        : this(ProcessPlatformSelector.Default, TimeSpan.FromSeconds(1), snapshotter: null)
    {
    }

    internal CliProcessTerminator(IProcessPlatform platform, TimeSpan startTimeTolerance, ProcessTreeSnapshotter? snapshotter)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        ArgumentOutOfRangeException.ThrowIfLessThan(startTimeTolerance, TimeSpan.Zero);
        _startTimeTolerance = startTimeTolerance;
        _snapshotter = snapshotter ?? new ProcessTreeSnapshotter(platform, startTimeTolerance, containmentLookup: null, logger: null);
    }

    /// <summary>
    /// Gets a shared terminator for the running operating system with default options.
    /// </summary>
    public static CliProcessTerminator Default => SharedDefault.Value;

    /// <summary>
    /// Ends one process after confirming its identity.
    /// </summary>
    /// <param name="identity">The process id and start time recorded earlier.</param>
    /// <param name="options">Termination options. Defaults are used when <see langword="null" />.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The typed result. The process is killed only when its identity matches.</returns>
    public async Task<CliProcessTerminationResult> TerminateProcessAsync(
        ProcessIdentity identity,
        CliProcessTerminationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exitWait = ValidatedExitWait(options);

        var item = await TerminateOneAsync(identity, exitWait).ConfigureAwait(false);
        return new CliProcessTerminationResult(identity, item.Outcome, CliProcessTerminationMethod.Single, [item], item.Message);
    }

    /// <summary>
    /// Ends a process and its descendants after confirming the root's identity. Descendants are ended before their parents.
    /// </summary>
    /// <param name="identity">The root's process id and start time recorded earlier.</param>
    /// <param name="options">Termination options. Defaults are used when <see langword="null" />.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The overall outcome and one item per process attempted or checked.</returns>
    public async Task<CliProcessTerminationResult> TerminateProcessTreeAsync(
        ProcessIdentity identity,
        CliProcessTerminationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exitWait = ValidatedExitWait(options);

        // Verify the root before anything is read or killed, so a reused root id ends nothing.
        var rootCheck = CheckIdentity(identity);
        if (rootCheck is not null)
        {
            return new CliProcessTerminationResult(identity, rootCheck.Outcome, CliProcessTerminationMethod.TreeWalk, [rootCheck], rootCheck.Message);
        }

        var snapshot = _snapshotter.CaptureCore(
            new ProcessTreeSnapshotRequest
            {
                Roots = [identity],
                Options = new ProcessTreeSnapshotOptions { IncludeCommandLine = false, MaxProcessesPerRoot = SubtreeCapacity },
            },
            cancellationToken);

        var items = new List<CliProcessTerminationItem>();
        var rootSnapshot = snapshot.Roots[0];
        if (rootSnapshot.Status == ProcessTreeRootStatus.Found)
        {
            // Breadth-first order reversed puts every descendant before its parent, so leaves are ended first.
            foreach (var member in rootSnapshot.Processes.Where(entry => entry.Membership != ProcessMembership.Root).Reverse())
            {
                items.Add(await TerminateMemberAsync(member, exitWait).ConfigureAwait(false));
            }
        }

        var rootItem = await TerminateOneAsync(identity, exitWait).ConfigureAwait(false);
        items.Add(rootItem);

        var outcome = AggregateTreeOutcome(rootItem, items);
        var message = outcome == CliProcessTerminationOutcome.Terminated ? null : rootItem.Message ?? SummarizeIncomplete(items);
        return new CliProcessTerminationResult(identity, outcome, CliProcessTerminationMethod.TreeWalk, items, message);
    }

    /// <summary>
    /// Ends a process tree held in a Windows Job Object. The root identity is verified first, then each member is recorded
    /// with the start time it had, the Job ends all members at once, and each one is waited for.
    /// </summary>
    internal async Task<CliProcessTerminationResult> TerminateContainedTreeAsync(
        ProcessIdentity identity,
        IWindowsProcessJob job,
        CliProcessTerminationOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(job);
        var exitWait = ValidatedExitWait(options);

        // Every member of this Job was assigned to it, so the Job can end the members of a root that already exited, such as an orphaned
        // descendant, without touching an unrelated process. Any other identity problem returns first, with nothing terminated.
        var rootCheck = CheckIdentity(identity);
        if (rootCheck is not null && rootCheck.Outcome is not CliProcessTerminationOutcome.AlreadyExited)
        {
            return new CliProcessTerminationResult(identity, rootCheck.Outcome, CliProcessTerminationMethod.Containment, [rootCheck], rootCheck.Message);
        }

        // Start times are read before the Job ends anything, so each item carries the identity it was checked against.
        var members = new List<(int Pid, DateTimeOffset? StartedAtUtc)>();
        foreach (var pid in job.ListMemberProcessIds())
        {
            if (pid == identity.Pid)
            {
                continue;
            }

            members.Add((pid, _platform.ReadDetails(pid, includeCommandLine: false)?.StartedAtUtc));
        }

        try
        {
            job.Terminate();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ObjectDisposedException or InvalidOperationException)
        {
            var failed = new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.Failed, $"The Job Object could not be terminated: {ex.Message}");
            return new CliProcessTerminationResult(identity, CliProcessTerminationOutcome.Failed, CliProcessTerminationMethod.Containment, [failed], failed.Message);
        }

        var items = new List<CliProcessTerminationItem>();
        foreach (var (pid, startedAtUtc) in members)
        {
            var memberIdentity = new ProcessIdentity(pid, startedAtUtc ?? default);
            var exited = await WaitForExitAsync(pid, exitWait).ConfigureAwait(false);
            items.Add(exited
                ? new CliProcessTerminationItem(memberIdentity, CliProcessTerminationOutcome.Terminated, null)
                : new CliProcessTerminationItem(memberIdentity, CliProcessTerminationOutcome.Failed, $"The process did not exit within {exitWait}."));
        }

        CliProcessTerminationItem rootItem;
        if (rootCheck is not null)
        {
            // The root had already exited before the Job ended anything.
            rootItem = rootCheck;
        }
        else
        {
            var rootExited = await WaitForExitAsync(identity.Pid, exitWait).ConfigureAwait(false);
            rootItem = rootExited
                ? new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.Terminated, null)
                : new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.Failed, $"The process did not exit within {exitWait}.");
        }

        items.Add(rootItem);

        var outcome = AggregateTreeOutcome(rootItem, items);
        var message = outcome == CliProcessTerminationOutcome.Terminated ? null : rootItem.Message ?? SummarizeIncomplete(items);
        return new CliProcessTerminationResult(identity, outcome, CliProcessTerminationMethod.Containment, items, message);
    }

    private static CliProcessTerminationOutcome AggregateTreeOutcome(CliProcessTerminationItem rootItem, IReadOnlyList<CliProcessTerminationItem> items)
    {
        var rootEnded = rootItem.Outcome is CliProcessTerminationOutcome.Terminated or CliProcessTerminationOutcome.AlreadyExited;
        if (!rootEnded)
        {
            return rootItem.Outcome;
        }

        // A descendant whose id was reused has already exited, so it counts as ended.
        var everyoneEnded = items.All(item => item.Outcome is CliProcessTerminationOutcome.Terminated
            or CliProcessTerminationOutcome.AlreadyExited
            or CliProcessTerminationOutcome.IdentityMismatch);
        return everyoneEnded ? CliProcessTerminationOutcome.Terminated : CliProcessTerminationOutcome.PartiallyTerminated;
    }

    private static string? SummarizeIncomplete(IReadOnlyList<CliProcessTerminationItem> items)
    {
        var incomplete = items.Count(item => item.Outcome is CliProcessTerminationOutcome.AccessDenied or CliProcessTerminationOutcome.Failed);
        return incomplete == 0 ? null : $"{incomplete} process(es) in the tree could not be terminated.";
    }

    private async Task<CliProcessTerminationItem> TerminateMemberAsync(ProcessSnapshotEntry member, TimeSpan exitWait)
    {
        if (member.StartedAtUtc is not { } startedAtUtc)
        {
            // The identity of an unreadable process is unknown, so nothing is sent to it. The start time is shown as default.
            return new CliProcessTerminationItem(
                new ProcessIdentity(member.Pid, default),
                CliProcessTerminationOutcome.AccessDenied,
                "The start time of the process cannot be read, so its identity cannot be verified. Nothing was terminated.");
        }

        return await TerminateOneAsync(new ProcessIdentity(member.Pid, startedAtUtc), exitWait).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the identity of a root without killing anything. Returns <see langword="null" /> when the root matches and may be terminated.
    /// </summary>
    private CliProcessTerminationItem? CheckIdentity(ProcessIdentity identity)
    {
        var details = _platform.ReadDetails(identity.Pid, includeCommandLine: false);
        if (details is null)
        {
            return new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.AlreadyExited, "The process is no longer running.");
        }

        if (details.StartedAtUtc is not { } startedAtUtc)
        {
            return new CliProcessTerminationItem(
                identity,
                CliProcessTerminationOutcome.AccessDenied,
                "The start time of the process cannot be read, so its identity cannot be verified. Nothing was terminated.");
        }

        if (!ProcessIdentity.StartTimesMatch(startedAtUtc, identity.StartedAtUtc, _startTimeTolerance))
        {
            return new CliProcessTerminationItem(
                identity,
                CliProcessTerminationOutcome.IdentityMismatch,
                "A different process now has this process id. Nothing was terminated.");
        }

        return null;
    }

    private async Task<CliProcessTerminationItem> TerminateOneAsync(ProcessIdentity identity, TimeSpan exitWait)
    {
        // The identity is checked again here, immediately before the kill, for every process including the root.
        var mismatch = CheckIdentity(identity);
        if (mismatch is not null)
        {
            return mismatch;
        }

        var killed = _platform.Kill(identity.Pid);
        switch (killed.Status)
        {
            case ProcessKillStatus.AlreadyExited:
                return new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.AlreadyExited, killed.Message ?? "The process exited before it could be terminated.");
            case ProcessKillStatus.AccessDenied:
                return new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.AccessDenied, killed.Message ?? "Access to the process was denied.");
            case ProcessKillStatus.Failed:
                return new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.Failed, killed.Message ?? "The process could not be terminated.");
        }

        if (await WaitForExitAsync(identity.Pid, exitWait).ConfigureAwait(false))
        {
            return new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.Terminated, null);
        }

        return new CliProcessTerminationItem(identity, CliProcessTerminationOutcome.Failed, $"The process did not exit within {exitWait}.");
    }

    private async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (_platform.ReadDetails(pid, includeCommandLine: false) is null)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    private static TimeSpan ValidatedExitWait(CliProcessTerminationOptions? options)
    {
        var exitWait = (options ?? new CliProcessTerminationOptions()).ExitWaitTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(exitWait, TimeSpan.Zero);
        return exitWait;
    }
}
