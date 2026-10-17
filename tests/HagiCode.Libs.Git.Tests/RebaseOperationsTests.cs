namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 2.5 (rebase): refusal on a dirty tree, no-upstream, success, and automatic abort on conflict.</summary>
[TestFixture]
public sealed class RebaseOperationsTests : GitTestBase
{
    [Test]
    public async Task Rebase_refuses_a_dirty_working_tree_and_leaves_the_repository_unchanged()
    {
        var remote = NewBareRemote();
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "remote", "add", "origin", remote);
        Git.Must(repository, "push", "--quiet", "-u", "origin", "main");
        WriteFile(repository, "README.md", "dirty\n");
        var headBefore = Git.Must(repository, "rev-parse", "HEAD");

        var result = await Client.RebaseAsync(repository);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.WorkingTreeDirty));
        Assert.That(result.WorkingTreeClean, Is.False);
        Assert.That(result.CurrentBranch, Is.EqualTo("main"));
        Assert.That(Git.Must(repository, "rev-parse", "HEAD"), Is.EqualTo(headBefore));
        Assert.That(File.ReadAllText(Path.Combine(repository, "README.md")), Is.EqualTo("dirty\n"));
    }

    [Test]
    public async Task Rebase_without_an_upstream_reports_no_upstream_and_a_clean_tree()
    {
        var repository = NewRepositoryWithCommit();

        var result = await Client.RebaseAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.NoUpstream));
        Assert.That(result.RemoteBranchAvailable, Is.False);
        Assert.That(result.WorkingTreeClean, Is.True);
    }

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task Rebase_replays_local_commits_onto_the_upstream()
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

        var result = await Client.RebaseAsync(origin);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.TargetBranch, Is.EqualTo("origin/main"));
        Assert.That(result.WorkingTreeClean, Is.True);
        Assert.That(File.Exists(Path.Combine(origin, "theirs.txt")), Is.True);
        Assert.That(File.Exists(Path.Combine(origin, "mine.txt")), Is.True);
    }

    [Test]
    public async Task Rebase_that_conflicts_is_aborted_and_the_branch_is_restored()
    {
        var remote = NewBareRemote();
        var origin = NewRepositoryWithCommit();
        Git.Must(origin, "remote", "add", "origin", remote);
        Git.Must(origin, "push", "--quiet", "-u", "origin", "main");

        var other = CloneRemote(remote, "other");
        WriteFile(other, "README.md", "theirs\n");
        Git.Must(other, "commit", "--quiet", "-am", "theirs");
        Git.Must(other, "push", "--quiet");

        WriteFile(origin, "README.md", "mine\n");
        Git.Must(origin, "commit", "--quiet", "-am", "mine");
        var localHead = Git.Must(origin, "rev-parse", "HEAD");

        var result = await Client.RebaseAsync(origin);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.RebaseConflictAborted));
        Assert.That(result.RebaseAborted, Is.True);
        Assert.That(result.WorkingTreeClean, Is.True);
        Assert.That(Git.Must(origin, "rev-parse", "HEAD"), Is.EqualTo(localHead), "the branch points at its original commit");
        Assert.That(Directory.Exists(Path.Combine(origin, ".git", "rebase-merge")), Is.False, "no rebase is left in progress");
        Assert.That(Directory.Exists(Path.Combine(origin, ".git", "rebase-apply")), Is.False);
    }
}
