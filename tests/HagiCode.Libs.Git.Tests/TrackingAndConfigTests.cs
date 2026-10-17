using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests;

/// <summary>The configuration read and the upstream ahead/behind counts, used by the backend co-author policy and the scanner.</summary>
[TestFixture]
public sealed class TrackingAndConfigTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task GetConfigValue_reads_local_and_global_scopes_separately_and_returns_null_when_unset()
    {
        var repository = NewRepository();
        Git.Must(repository, "config", "--local", "coAuthoredBy.name", "Local Bot");
        Git.Must(Environment.Root, "config", "--file", Environment.GlobalConfigPath, "coAuthoredBy.name", "Global Bot");

        var local = await Client.GetConfigValueAsync(repository, "coAuthoredBy.name", GitConfigScope.Local);
        var global = await Client.GetConfigValueAsync(repository, "coAuthoredBy.name", GitConfigScope.Global);
        var unset = await Client.GetConfigValueAsync(repository, "coAuthoredBy.email", GitConfigScope.Local);

        Assert.That(local.Value, Is.EqualTo("Local Bot"));
        Assert.That(global.Value, Is.EqualTo("Global Bot"));
        Assert.That(unset.Success, Is.True);
        Assert.That(unset.Value, Is.Null);
    }

    [Test]
    public async Task GetConfigValue_never_writes_and_rejects_bad_arguments()
    {
        var repository = NewRepository();
        var localBefore = File.ReadAllText(Path.Combine(repository, ".git", "config"));

        var read = await Client.GetConfigValueAsync(repository, "user.name", GitConfigScope.Local);
        var emptyKey = await Client.GetConfigValueAsync(repository, " ", GitConfigScope.Local);
        var noScope = await Client.GetConfigValueAsync(repository, "user.name", GitConfigScope.None);
        var notRepo = await Client.GetConfigValueAsync(Environment.Root, "user.name", GitConfigScope.Local);

        Assert.That(read.Success, Is.True);
        Assert.That(emptyKey.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidArgument));
        Assert.That(noScope.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidArgument));
        Assert.That(notRepo.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
        Assert.That(File.ReadAllText(Path.Combine(repository, ".git", "config")), Is.EqualTo(localBefore));
    }

    [Test]
    public async Task GetTrackingInfo_is_null_without_an_upstream_and_for_a_detached_head()
    {
        var repository = NewRepositoryWithCommit();
        var noUpstream = await Client.GetTrackingInfoAsync(repository);
        Git.Must(repository, "checkout", "--quiet", "--detach");
        var detached = await Client.GetTrackingInfoAsync(repository);

        Assert.That(noUpstream.Success, Is.True);
        Assert.That(noUpstream.Value, Is.Null);
        Assert.That(detached.Value, Is.Null);
    }

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task GetTrackingInfo_counts_commits_ahead_of_and_behind_the_upstream()
    {
        var remote = NewBareRemote();
        var origin = NewRepositoryWithCommit();
        Git.Must(origin, "remote", "add", "origin", remote);
        Git.Must(origin, "push", "--quiet", "-u", "origin", "main");

        var other = CloneRemote(remote, "other");
        WriteFile(other, "theirs.txt", "x\n");
        Git.Must(other, "add", "theirs.txt");
        Git.Must(other, "commit", "--quiet", "-m", "theirs");
        Git.Must(other, "push", "--quiet");

        WriteFile(origin, "mine-1.txt", "x\n");
        Git.Must(origin, "add", "mine-1.txt");
        Git.Must(origin, "commit", "--quiet", "-m", "mine 1");
        WriteFile(origin, "mine-2.txt", "x\n");
        Git.Must(origin, "add", "mine-2.txt");
        Git.Must(origin, "commit", "--quiet", "-m", "mine 2");
        Git.Must(origin, "fetch", "--quiet", "origin");

        var result = await Client.GetTrackingInfoAsync(origin);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value, Is.EqualTo(new GitTrackingInfo("origin/main", Ahead: 2, Behind: 1)));
    }
}
