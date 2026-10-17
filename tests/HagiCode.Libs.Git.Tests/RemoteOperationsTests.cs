using System.Diagnostics;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 2.5 (push and pull): upstream handling and failure classification against a local bare remote.</summary>
[TestFixture]
public sealed class RemoteOperationsTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task Push_succeeds_and_the_remote_receives_the_commit()
    {
        var remote = NewBareRemote();
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "remote", "add", "origin", remote);
        Git.Must(repository, "push", "--quiet", "-u", "origin", "main");
        WriteFile(repository, "next.txt", "next\n");
        await Client.StageAsync(repository, "next.txt");
        var commit = (await Client.CommitAsync(repository, "next")).Value!.CommitSha;

        var result = await Client.PushAsync(repository);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value!.Branch, Is.EqualTo("main"));
        Assert.That(result.Value.Upstream, Is.EqualTo("origin/main"));
        Assert.That(Git.Must(remote, "rev-parse", "refs/heads/main"), Is.EqualTo(commit));
    }

    [Test]
    public async Task Push_without_an_upstream_fails_with_no_upstream_and_never_touches_the_network()
    {
        var repository = NewRepositoryWithCommit();

        var stopwatch = Stopwatch.StartNew();
        var result = await Client.PushAsync(repository);
        stopwatch.Stop();

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.NoUpstream));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)));
    }

    [Test]
    public async Task Push_that_is_not_a_fast_forward_is_classified_as_non_fast_forward()
    {
        var remote = NewBareRemote();
        var origin = NewRepositoryWithCommit();
        Git.Must(origin, "remote", "add", "origin", remote);
        Git.Must(origin, "push", "--quiet", "-u", "origin", "main");

        var other = CloneRemote(remote, "other");
        WriteFile(other, "theirs.txt", "theirs\n");
        Git.Must(other, "add", "theirs.txt");
        Git.Must(other, "commit", "--quiet", "-m", "theirs");
        Git.Must(other, "push", "--quiet");

        WriteFile(origin, "mine.txt", "mine\n");
        Git.Must(origin, "add", "mine.txt");
        Git.Must(origin, "commit", "--quiet", "-m", "mine");

        var result = await Client.PushAsync(origin);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.NonFastForward));
        Assert.That(result.Failure.Details, Is.Not.Null.And.Not.Empty, "the captured output travels with the failure");
    }

    [Test]
    public async Task Pull_fast_forwards_when_the_remote_is_ahead()
    {
        var remote = NewBareRemote();
        var origin = NewRepositoryWithCommit();
        Git.Must(origin, "remote", "add", "origin", remote);
        Git.Must(origin, "push", "--quiet", "-u", "origin", "main");

        var other = CloneRemote(remote, "other");
        WriteFile(other, "theirs.txt", "theirs\n");
        Git.Must(other, "add", "theirs.txt");
        Git.Must(other, "commit", "--quiet", "-m", "theirs");
        Git.Must(other, "push", "--quiet");

        var result = await Client.PullAsync(origin);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(Git.Must(origin, "rev-parse", "HEAD"), Is.EqualTo(Git.Must(remote, "rev-parse", "refs/heads/main")));
    }

    [Test]
    public async Task Pull_with_diverged_history_is_classified_as_fast_forward_required()
    {
        var remote = NewBareRemote();
        var origin = NewRepositoryWithCommit();
        Git.Must(origin, "remote", "add", "origin", remote);
        Git.Must(origin, "push", "--quiet", "-u", "origin", "main");

        var other = CloneRemote(remote, "other");
        WriteFile(other, "theirs.txt", "theirs\n");
        Git.Must(other, "add", "theirs.txt");
        Git.Must(other, "commit", "--quiet", "-m", "theirs");
        Git.Must(other, "push", "--quiet");

        WriteFile(origin, "mine.txt", "mine\n");
        Git.Must(origin, "add", "mine.txt");
        Git.Must(origin, "commit", "--quiet", "-m", "mine");

        var result = await Client.PullAsync(origin);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.FastForwardRequired));
    }

    [Test]
    public async Task Pull_without_an_upstream_fails_with_no_upstream()
    {
        var repository = NewRepositoryWithCommit();

        var result = await Client.PullAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.NoUpstream));
    }

    [Test]
    public async Task Remote_access_that_would_prompt_fails_fast_instead_of_waiting()
    {
        var repository = NewRepositoryWithCommit();
        // Connection refused on loopback fails immediately; the point is that no credential prompt can block the call.
        // The upstream is configured by hand so the setup itself never touches the network.
        Git.Must(repository, "remote", "add", "origin", "https://127.0.0.1:1/unreachable.git");
        Git.Must(repository, "config", "branch.main.remote", "origin");
        Git.Must(repository, "config", "branch.main.merge", "refs/heads/main");

        var stopwatch = Stopwatch.StartNew();
        var result = await Client.PushAsync(repository);
        stopwatch.Stop();

        Assert.That(result.Success, Is.False);
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromMinutes(1)));
    }
}
