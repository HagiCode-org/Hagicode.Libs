namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 1.5: the tracking fields the single status read reports match <see cref="IGitClient.GetTrackingInfoAsync" />.</summary>
[TestFixture]
public sealed class ProbeDirtyTrackingTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task Tracking_reports_ahead_two_and_behind_one_like_GetTrackingInfo()
    {
        var origin = NewRepositoryWithUpstream(out var remote);
        var other = CloneRemote(remote, "other");
        WriteFile(other, "theirs.txt", "x\n");
        Git.Must(other, "add", "theirs.txt");
        Git.Must(other, "commit", "--quiet", "-m", "theirs");
        Git.Must(other, "push", "--quiet");
        CommitLocal(origin, "mine-1.txt");
        CommitLocal(origin, "mine-2.txt");
        Git.Must(origin, "fetch", "--quiet", "origin");

        await AssertTrackingMatchesAsync(origin, new GitTrackingInfo("origin/main", Ahead: 2, Behind: 1));
    }

    [Test]
    public async Task Tracking_reports_in_sync_with_zero_counts()
    {
        var origin = NewRepositoryWithUpstream(out _);

        await AssertTrackingMatchesAsync(origin, new GitTrackingInfo("origin/main", Ahead: 0, Behind: 0));
    }

    [Test]
    public async Task Tracking_is_null_without_an_upstream()
    {
        var repository = NewRepositoryWithCommit();

        await AssertTrackingMatchesAsync(repository, expected: null);
    }

    [Test]
    public async Task Tracking_is_null_when_the_upstream_ref_was_deleted()
    {
        var origin = NewRepositoryWithUpstream(out _);
        Git.Must(origin, "update-ref", "-d", "refs/remotes/origin/main");

        await AssertTrackingMatchesAsync(origin, expected: null);
    }

    private string NewRepositoryWithUpstream(out string remote)
    {
        remote = NewBareRemote($"remote-{Guid.NewGuid():N}.git");
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "remote", "add", "origin", remote);
        Git.Must(repository, "push", "--quiet", "-u", "origin", "main");
        return repository;
    }

    private void CommitLocal(string repository, string file)
    {
        WriteFile(repository, file, "x\n");
        Git.Must(repository, "add", file);
        Git.Must(repository, "commit", "--quiet", "-m", file);
    }

    private async Task AssertTrackingMatchesAsync(string repository, GitTrackingInfo? expected)
    {
        var probe = await Client.ProbeDirtyAsync(repository);
        var tracking = await Client.GetTrackingInfoAsync(repository);

        Assert.That(probe.Success, Is.True, probe.Failure?.Message);
        Assert.That(tracking.Success, Is.True, tracking.Failure?.Message);
        Assert.That(probe.Value!.Tracking, Is.EqualTo(tracking.Value), "the probe and GetTrackingInfoAsync must agree");
        Assert.That(probe.Value.Tracking, Is.EqualTo(expected));
    }
}
