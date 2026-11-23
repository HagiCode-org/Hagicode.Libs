using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Termination;
using HagiCode.Libs.Core.Process.Windows;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CliProcessTerminatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly CliProcessTerminationOptions QuickExit = new() { ExitWaitTimeout = TimeSpan.FromMilliseconds(200) };

    // 100 -> 101 -> 103 ; 100 -> 102
    private static FakeProcessPlatform BuildTree() => new FakeProcessPlatform()
        .Add(100, 1, "root", Start)
        .Add(101, 100, "child-a", Start.AddSeconds(1))
        .Add(102, 100, "child-b", Start.AddSeconds(2))
        .Add(103, 101, "grandchild", Start.AddSeconds(3));

    private static CliProcessTerminator Create(FakeProcessPlatform platform) => new(platform, TimeSpan.FromSeconds(1), null);

    [Test]
    public async Task A_contained_tree_whose_root_already_exited_still_ends_its_orphaned_members_through_the_job()
    {
        // The root (100) is gone, and its orphan (101) is still a member of the Job.
        var platform = new FakeProcessPlatform().Add(101, 100, "orphan", Start.AddSeconds(1));
        var job = new FakeContainmentJob(platform, 101);

        var result = await Create(platform).TerminateContainedTreeAsync(new ProcessIdentity(100, Start), job, QuickExit, CancellationToken.None);

        result.Method.ShouldBe(CliProcessTerminationMethod.Containment);
        result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
        platform.IsAlive(101).ShouldBeFalse();
    }

    [Test]
    public async Task A_contained_tree_whose_root_pid_was_reused_terminates_nothing()
    {
        // Pid 100 now belongs to a different process, so the identity check stops the Job from being ended.
        var platform = new FakeProcessPlatform()
            .Add(100, 1, "unrelated", Start.AddMinutes(5))
            .Add(101, 100, "member", Start.AddSeconds(1));
        var job = new FakeContainmentJob(platform, 101);

        var result = await Create(platform).TerminateContainedTreeAsync(new ProcessIdentity(100, Start), job, QuickExit, CancellationToken.None);

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.IdentityMismatch);
        platform.IsAlive(101).ShouldBeTrue();
        platform.IsAlive(100).ShouldBeTrue();
    }

    /// <summary>A Job whose members end when it is terminated, the way a real Job does, over the in-memory process table.</summary>
    private sealed class FakeContainmentJob : IWindowsProcessJob
    {
        private readonly FakeProcessPlatform _platform;
        private readonly List<int> _members;

        public FakeContainmentJob(FakeProcessPlatform platform, params int[] members)
        {
            _platform = platform;
            _members = [.. members];
        }

        public bool TryAssignProcess(int pid, out int win32Error)
        {
            _members.Add(pid);
            win32Error = 0;
            return true;
        }

        public IReadOnlyList<int> ListMemberProcessIds() => _members.ToArray();

        public void Terminate()
        {
            foreach (var pid in _members)
            {
                _platform.Kill(pid);
            }
        }

        public void ReleaseKeepingMembers()
        {
        }

        public void Dispose()
        {
        }
    }

    [Test]
    public async Task A_live_process_with_a_matching_identity_is_terminated()
    {
        var platform = new FakeProcessPlatform().Add(300, 1, "service", Start);

        var result = await Create(platform).TerminateProcessAsync(new ProcessIdentity(300, Start));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
        result.Method.ShouldBe(CliProcessTerminationMethod.Single);
        platform.KillRequests.ShouldBe(new[] { 300 });
        platform.IsAlive(300).ShouldBeFalse();
    }

    [Test]
    public async Task A_process_that_is_already_gone_is_reported_as_already_exited_without_a_kill()
    {
        var platform = new FakeProcessPlatform();

        var result = await Create(platform).TerminateProcessAsync(new ProcessIdentity(300, Start));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.AlreadyExited);
        platform.KillRequests.ShouldBeEmpty();
    }

    [Test]
    public async Task A_reused_process_id_is_never_killed()
    {
        var platform = new FakeProcessPlatform().Add(300, 1, "unrelated", Start);

        var result = await Create(platform).TerminateProcessAsync(new ProcessIdentity(300, Start.AddHours(-1)));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.IdentityMismatch);
        platform.KillRequests.ShouldBeEmpty();
        platform.IsAlive(300).ShouldBeTrue();
    }

    [Test]
    public async Task A_denied_kill_is_reported_as_access_denied()
    {
        var platform = new FakeProcessPlatform().Add(300, 1, "protected", Start);
        platform.DeniedKills.Add(300);

        var result = await Create(platform).TerminateProcessAsync(new ProcessIdentity(300, Start));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.AccessDenied);
    }

    [Test]
    public async Task A_process_that_survives_the_kill_past_the_exit_wait_is_failed()
    {
        var platform = new FakeProcessPlatform().Add(300, 1, "stubborn", Start);
        platform.KillLeavesRunning.Add(300);

        var result = await Create(platform).TerminateProcessAsync(new ProcessIdentity(300, Start), QuickExit);

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.Failed);
        result.Message!.ShouldContain("did not exit");
    }

    [Test]
    public async Task A_tree_is_ended_leaves_first_and_the_root_last()
    {
        var platform = BuildTree();

        var result = await Create(platform).TerminateProcessTreeAsync(new ProcessIdentity(100, Start));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
        result.Method.ShouldBe(CliProcessTerminationMethod.TreeWalk);
        platform.KillRequests.ShouldBe(new[] { 103, 102, 101, 100 });
        result.Items.Select(item => item.Identity.Pid).ToArray().ShouldBe(new[] { 103, 102, 101, 100 });
    }

    [Test]
    public async Task A_descendant_whose_id_was_reused_before_its_turn_is_not_killed()
    {
        var platform = BuildTree();
        platform.AfterKill = pid =>
        {
            // Killing the grandchild frees its place, and the kernel hands pid 102 to a different process.
            if (pid == 103)
            {
                platform.Add(102, 100, "reused", Start.AddSeconds(50));
            }
        };

        var result = await Create(platform).TerminateProcessTreeAsync(new ProcessIdentity(100, Start));

        platform.KillRequests.ShouldNotContain(102);
        result.Items.Single(item => item.Identity.Pid == 102).Outcome.ShouldBe(CliProcessTerminationOutcome.IdentityMismatch);
        result.Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
    }

    [Test]
    public async Task A_root_identity_mismatch_stops_the_whole_tree_request()
    {
        var platform = BuildTree();

        var result = await Create(platform).TerminateProcessTreeAsync(new ProcessIdentity(100, Start.AddHours(-1)));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.IdentityMismatch);
        platform.KillRequests.ShouldBeEmpty();
    }

    [Test]
    public async Task A_descendant_that_cannot_be_killed_leaves_the_tree_partially_terminated()
    {
        var platform = BuildTree();
        platform.DeniedKills.Add(101);

        var result = await Create(platform).TerminateProcessTreeAsync(new ProcessIdentity(100, Start));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.PartiallyTerminated);
        result.Items.Single(item => item.Identity.Pid == 101).Outcome.ShouldBe(CliProcessTerminationOutcome.AccessDenied);
        result.Items.Single(item => item.Identity.Pid == 100).Outcome.ShouldBe(CliProcessTerminationOutcome.Terminated);
    }

    [Test]
    public async Task A_cancelled_request_throws_before_any_work_starts()
    {
        var platform = new FakeProcessPlatform().Add(300, 1, "service", Start);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Create(platform).TerminateProcessAsync(new ProcessIdentity(300, Start), null, cancellation.Token));

        platform.KillRequests.ShouldBeEmpty();
    }

    [Test]
    public async Task A_tree_whose_root_cannot_be_killed_reports_access_denied_for_the_whole_tree()
    {
        var platform = BuildTree();
        platform.DeniedKills.Add(100);

        var result = await Create(platform).TerminateProcessTreeAsync(new ProcessIdentity(100, Start));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.AccessDenied);
    }

    [Test]
    public async Task A_process_whose_start_time_cannot_be_read_is_denied_and_not_killed()
    {
        var platform = new FakeProcessPlatform().Add(300, 1, "locked", Start, startTimeUnreadable: true);

        var result = await Create(platform).TerminateProcessAsync(new ProcessIdentity(300, Start));

        result.Outcome.ShouldBe(CliProcessTerminationOutcome.AccessDenied);
        platform.KillRequests.ShouldBeEmpty();
        platform.IsAlive(300).ShouldBeTrue();
    }
}
