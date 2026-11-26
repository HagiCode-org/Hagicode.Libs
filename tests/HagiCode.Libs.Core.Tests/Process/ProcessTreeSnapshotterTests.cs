using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class ProcessTreeSnapshotterTests
{
    private static readonly DateTimeOffset RootStart = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    // 100 (root) -> 101 -> 103 ; 100 -> 102 ; 200 is unrelated.
    private static FakeProcessPlatform BuildTree() => new FakeProcessPlatform()
        .Add(100, 1, "root", RootStart, commandLine: "root --serve")
        .Add(101, 100, "child-a", RootStart.AddSeconds(1))
        .Add(102, 100, "child-b", RootStart.AddSeconds(2))
        .Add(103, 101, "grandchild", RootStart.AddSeconds(3))
        .Add(200, 1, "unrelated", RootStart.AddSeconds(5));

    private static ProcessTreeSnapshotter Create(FakeProcessPlatform platform) =>
        new(platform, TimeSpan.FromSeconds(1), containmentLookup: null, logger: null);

    [Test]
    public async Task A_found_root_lists_its_tree_parent_first_with_depths()
    {
        var snapshot = await Create(BuildTree()).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
        });

        var root = snapshot.Roots.Single();
        root.Status.ShouldBe(ProcessTreeRootStatus.Found);
        root.Truncated.ShouldBeFalse();
        root.Processes
            .Select(entry => (entry.Pid, entry.Depth, entry.Membership))
            .ToArray()
            .ShouldBe(new[]
            {
                (100, 0, ProcessMembership.Root),
                (101, 1, ProcessMembership.Descendant),
                (102, 1, ProcessMembership.Descendant),
                (103, 2, ProcessMembership.Descendant),
            });
    }

    [Test]
    public async Task A_process_outside_the_tree_is_never_listed_or_read()
    {
        var platform = BuildTree();
        var snapshot = await Create(platform).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
        });

        snapshot.Roots.Single().Processes.Select(entry => entry.Pid).ShouldNotContain(200);
        platform.DetailRequests.Select(request => request.Pid).ShouldNotContain(200);
    }

    [Test]
    public async Task A_missing_root_is_reported_as_not_found()
    {
        var snapshot = await Create(BuildTree()).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(999, RootStart)],
        });

        var root = snapshot.Roots.Single();
        root.Status.ShouldBe(ProcessTreeRootStatus.NotFound);
        root.Processes.ShouldBeEmpty();
    }

    [Test]
    public async Task A_root_with_a_different_start_time_is_an_identity_mismatch_and_lists_nothing()
    {
        var snapshot = await Create(BuildTree()).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart.AddHours(-1))],
        });

        var root = snapshot.Roots.Single();
        root.Status.ShouldBe(ProcessTreeRootStatus.IdentityMismatch);
        root.Processes.ShouldBeEmpty();
    }

    [Test]
    public async Task Several_roots_are_captured_in_one_table_read_and_keep_their_own_status()
    {
        var platform = BuildTree();
        var snapshot = await Create(platform).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart), new ProcessIdentity(999, RootStart), new ProcessIdentity(200, RootStart.AddSeconds(5))],
        });

        snapshot.Roots.Select(root => root.Status).ToArray().ShouldBe(new[]
        {
            ProcessTreeRootStatus.Found,
            ProcessTreeRootStatus.NotFound,
            ProcessTreeRootStatus.Found,
        });
        platform.TableReads.ShouldBe(1);
    }

    [Test]
    public async Task The_walk_stops_at_max_processes_per_root_and_marks_the_result_truncated()
    {
        var snapshot = await Create(BuildTree()).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
            Options = new ProcessTreeSnapshotOptions { MaxProcessesPerRoot = 2 },
        });

        var root = snapshot.Roots.Single();
        root.Processes.Select(entry => entry.Pid).ToArray().ShouldBe(new[] { 100, 101 });
        root.Truncated.ShouldBeTrue();
    }

    [Test]
    public async Task Command_lines_are_not_read_when_they_are_not_requested()
    {
        var platform = BuildTree();
        var snapshot = await Create(platform).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
            Options = new ProcessTreeSnapshotOptions { IncludeCommandLine = false },
        });

        snapshot.Roots.Single().Processes.ShouldAllBe(entry => entry.CommandLine == null);
        platform.DetailRequests.ShouldAllBe(request => request.IncludeCommandLine == false);
    }

    [Test]
    public async Task Command_lines_are_returned_as_read_when_requested()
    {
        var snapshot = await Create(BuildTree()).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
        });

        snapshot.Roots.Single().Processes.First().CommandLine.ShouldBe("root --serve");
    }

    [Test]
    public async Task A_child_that_started_before_its_parent_is_rejected_by_the_reuse_guard()
    {
        // 104 claims parent 101, but it started ten seconds before 101 did, so 101's id was reused and the link is not real.
        var platform = BuildTree().Add(104, 101, "reused-link", RootStart.AddSeconds(-10));
        var snapshot = await Create(platform).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
        });

        snapshot.Roots.Single().Processes.Select(entry => entry.Pid).ShouldNotContain(104);
    }

    [Test]
    public async Task An_unsupported_operating_system_fails_the_capture_with_the_documented_platform_error()
    {
        var snapshotter = new ProcessTreeSnapshotter(new UnsupportedProcessPlatform(), TimeSpan.FromSeconds(1), containmentLookup: null, logger: null);

        await Should.ThrowAsync<PlatformNotSupportedException>(() => snapshotter.CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
        }));
    }

    [Test]
    public async Task A_member_whose_start_time_cannot_be_read_is_listed_as_denied_without_an_identity()
    {
        var platform = BuildTree().Add(105, 100, "locked", RootStart.AddSeconds(4), startTimeUnreadable: true);
        var snapshot = await Create(platform).CaptureAsync(new ProcessTreeSnapshotRequest
        {
            Roots = [new ProcessIdentity(100, RootStart)],
        });

        var denied = snapshot.Roots.Single().Processes.Single(entry => entry.Pid == 105);
        denied.Accessibility.ShouldBe(ProcessAccessibility.Denied);
        denied.StartedAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task A_gone_root_whose_containment_still_has_processes_lists_only_those_as_exited()
    {
        // The root (100) is gone; 300 stays in its scope with a child 301. 200 is unrelated and never listed.
        var platform = new FakeProcessPlatform()
            .Add(300, 1, "worker", RootStart.AddSeconds(10))
            .Add(301, 300, "worker-child", RootStart.AddSeconds(11))
            .Add(200, 1, "unrelated", RootStart.AddSeconds(5));
        var root = new ProcessIdentity(100, RootStart);
        var snapshotter = new ProcessTreeSnapshotter(
            platform,
            TimeSpan.FromSeconds(1),
            containmentLookup: identity => identity == root ? [300] : [],
            logger: null);

        var snapshot = await snapshotter.CaptureAsync(new ProcessTreeSnapshotRequest { Roots = [root] });

        var result = snapshot.Roots.Single();
        result.Status.ShouldBe(ProcessTreeRootStatus.Exited);
        result.Processes
            .Select(entry => (entry.Pid, entry.Membership))
            .ToArray()
            .ShouldBe(new[] { (300, ProcessMembership.Containment), (301, ProcessMembership.Descendant) });
    }

    [Test]
    public async Task A_reused_root_id_with_remaining_containment_is_exited_and_the_new_process_is_not_listed()
    {
        var platform = new FakeProcessPlatform()
            .Add(100, 1, "someone-else", RootStart.AddHours(1))
            .Add(300, 1, "worker", RootStart.AddSeconds(10));
        var root = new ProcessIdentity(100, RootStart);
        var snapshotter = new ProcessTreeSnapshotter(
            platform,
            TimeSpan.FromSeconds(1),
            containmentLookup: identity => identity == root ? [100, 300] : [],
            logger: null);

        var result = (await snapshotter.CaptureAsync(new ProcessTreeSnapshotRequest { Roots = [root] })).Roots.Single();

        result.Status.ShouldBe(ProcessTreeRootStatus.Exited);
        result.Processes.Select(entry => entry.Pid).ShouldBe(new[] { 300 });
    }

    [Test]
    public async Task A_gone_root_with_an_empty_containment_keeps_its_plain_status()
    {
        var platform = new FakeProcessPlatform().Add(200, 1, "unrelated", RootStart);
        var snapshotter = new ProcessTreeSnapshotter(
            platform,
            TimeSpan.FromSeconds(1),
            containmentLookup: _ => [],
            logger: null);

        var result = (await snapshotter.CaptureAsync(new ProcessTreeSnapshotRequest { Roots = [new ProcessIdentity(100, RootStart)] })).Roots.Single();

        result.Status.ShouldBe(ProcessTreeRootStatus.NotFound);
        result.Processes.ShouldBeEmpty();
    }
}
