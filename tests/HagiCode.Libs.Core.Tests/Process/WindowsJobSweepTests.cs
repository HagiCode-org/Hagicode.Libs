using HagiCode.Libs.Core.Process.Windows;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>
/// The early-child sweep is plain logic over the process table, so it runs on Linux against the fake table and a fake Job.
/// </summary>
public sealed class WindowsJobSweepTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    [Test]
    public void Descendants_that_started_after_their_parent_join_the_job_at_every_depth()
    {
        var platform = new FakeProcessPlatform()
            .Add(100, 1, "root", Start)
            .Add(101, 100, "child", Start.AddSeconds(1))
            .Add(102, 101, "grandchild", Start.AddSeconds(2));
        var job = new SweepJob();

        var assigned = WindowsJobSweep.Sweep(job, platform, 100, Start, Tolerance, (_, _) => { });

        assigned.ShouldBe(2);
        job.Assigned.ToArray().ShouldBe(new[] { 101, 102 });
    }

    [Test]
    public void A_child_the_job_refuses_is_reported_with_its_win32_error_and_its_subtree_is_not_walked()
    {
        var platform = new FakeProcessPlatform()
            .Add(100, 1, "root", Start)
            .Add(101, 100, "child", Start.AddSeconds(1))
            .Add(102, 101, "grandchild", Start.AddSeconds(2));
        var job = new SweepJob { RefuseAssignment = 101, RefusalError = 5 };
        var failures = new List<(int Pid, int Error)>();

        var assigned = WindowsJobSweep.Sweep(job, platform, 100, Start, Tolerance, (pid, error) => failures.Add((pid, error)));

        assigned.ShouldBe(0);
        failures.ToArray().ShouldBe(new[] { (101, 5) });
        job.Assigned.ShouldBeEmpty();
    }

    [Test]
    public void A_child_that_started_before_its_parent_by_more_than_the_tolerance_is_not_assigned()
    {
        var platform = new FakeProcessPlatform()
            .Add(100, 1, "root", Start)
            .Add(101, 100, "reused-link", Start.AddSeconds(-10));
        var job = new SweepJob();

        var assigned = WindowsJobSweep.Sweep(job, platform, 100, Start, Tolerance, (_, _) => { });

        assigned.ShouldBe(0);
        job.Assigned.ShouldBeEmpty();
    }

    private sealed class SweepJob : IWindowsProcessJob
    {
        public List<int> Assigned { get; } = [];

        public int? RefuseAssignment { get; init; }

        public int RefusalError { get; init; }

        public bool TryAssignProcess(int pid, out int win32Error)
        {
            if (pid == RefuseAssignment)
            {
                win32Error = RefusalError;
                return false;
            }

            Assigned.Add(pid);
            win32Error = 0;
            return true;
        }

        public IReadOnlyList<int> ListMemberProcessIds() => Assigned.ToArray();

        public void Terminate()
        {
        }

        public void ReleaseKeepingMembers()
        {
        }

        public void Dispose()
        {
        }
    }
}
