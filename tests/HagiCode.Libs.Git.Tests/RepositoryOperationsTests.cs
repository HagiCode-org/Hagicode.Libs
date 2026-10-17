using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 2.1: repository validation, root discovery, remote, identity, branch and name checks against real repositories.</summary>
[TestFixture]
public sealed class RepositoryOperationsTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task IsRepository_is_true_for_a_repository_root_and_false_for_a_subfolder()
    {
        var repository = NewRepositoryWithCommit();
        var subfolder = Path.Combine(repository, "docs");
        Directory.CreateDirectory(subfolder);

        Assert.That(await Client.IsRepositoryAsync(repository), Is.True);
        Assert.That(await Client.IsRepositoryAsync(subfolder), Is.False,
            "a subfolder is not a repository root; discovery does not walk up for validity checks");
    }

    [Test]
    public async Task IsRepository_is_false_for_empty_and_missing_paths()
    {
        Assert.That(await Client.IsRepositoryAsync(string.Empty), Is.False);
        Assert.That(await Client.IsRepositoryAsync(Path.Combine(Environment.Root, "missing")), Is.False);
    }

    [Test]
    public async Task IsRepository_accepts_a_worktree_whose_git_entry_is_a_file()
    {
        var repository = NewRepositoryWithCommit();
        var worktree = Path.Combine(Environment.Root, "linked-worktree");
        Git.Must(repository, "worktree", "add", "-b", "feature", worktree);

        Assert.That(File.Exists(Path.Combine(worktree, ".git")), Is.True, "a worktree keeps a .git file, not a directory");
        Assert.That(await Client.IsRepositoryAsync(worktree), Is.True);
        Assert.That((await Client.GetCurrentBranchAsync(worktree)).Value, Is.EqualTo("feature"));
    }

    [Test]
    public async Task FindRepositoryRoot_walks_up_from_a_subfolder()
    {
        var repository = NewRepositoryWithCommit();
        var nested = Path.Combine(repository, "src", "deep");
        Directory.CreateDirectory(nested);

        var result = await Client.FindRepositoryRootAsync(nested);

        Assert.That(result.Success, Is.True);
        Assert.That(Path.GetFullPath(result.Value!), Is.EqualTo(Path.GetFullPath(repository)));
    }

    [Test]
    public async Task FindRepositoryRoot_fails_outside_any_repository()
    {
        var folder = Path.Combine(Environment.Root, "plain-folder");
        Directory.CreateDirectory(folder);

        var result = await Client.FindRepositoryRootAsync(folder);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
    }

    [Test]
    public async Task GetRemoteUrl_returns_null_without_an_origin_and_the_url_with_one()
    {
        var repository = NewRepositoryWithCommit();

        var before = await Client.GetRemoteUrlAsync(repository);
        Git.Must(repository, "remote", "add", "origin", "https://example.invalid/team/project.git");
        var after = await Client.GetRemoteUrlAsync(repository);

        Assert.That(before.Success, Is.True);
        Assert.That(before.Value, Is.Null);
        Assert.That(after.Value, Is.EqualTo("https://example.invalid/team/project.git"));
    }

    [Test]
    public async Task GetRemoteUrl_fails_for_a_path_that_is_not_a_repository()
    {
        var result = await Client.GetRemoteUrlAsync(Environment.Root);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
    }

    [Test]
    public async Task GetCurrentBranch_reports_an_unborn_branch_by_its_name()
    {
        var repository = NewRepository();

        var result = await Client.GetCurrentBranchAsync(repository);

        Assert.That(result.Value, Is.EqualTo("main"));
    }

    [Test]
    public async Task GetCurrentBranch_reports_HEAD_when_detached()
    {
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "checkout", "--quiet", "--detach");

        var result = await Client.GetCurrentBranchAsync(repository);

        Assert.That(result.Value, Is.EqualTo("HEAD"));
    }

    [Test]
    public async Task GetIdentity_reads_the_global_identity_with_its_scope()
    {
        var repository = NewRepository();

        var result = await Client.GetIdentityAsync(repository);

        Assert.That(result.Value!.Name, Is.EqualTo(TestGitEnvironment.TestName));
        Assert.That(result.Value.NameScope, Is.EqualTo(GitConfigScope.Global));
        Assert.That(result.Value.Email, Is.EqualTo(TestGitEnvironment.TestEmail));
        Assert.That(result.Value.EmailScope, Is.EqualTo(GitConfigScope.Global));
        Assert.That(result.Value.IsComplete, Is.True);
    }

    [Test]
    public async Task GetIdentity_prefers_local_over_global_for_each_value_independently()
    {
        var repository = NewRepository();
        Git.Must(repository, "config", "--local", "user.name", "Local Person");

        var result = await Client.GetIdentityAsync(repository);

        Assert.That(result.Value!.Name, Is.EqualTo("Local Person"));
        Assert.That(result.Value.NameScope, Is.EqualTo(GitConfigScope.Local));
        Assert.That(result.Value.Email, Is.EqualTo(TestGitEnvironment.TestEmail));
        Assert.That(result.Value.EmailScope, Is.EqualTo(GitConfigScope.Global));
    }

    [Test]
    public async Task GetIdentity_reports_none_and_writes_nothing_when_no_scope_sets_it()
    {
        using var bare = new TestGitEnvironment(withIdentity: false);
        var client = CreateClient(bare.Variables);
        var repository = Path.Combine(bare.Root, "repo");
        Directory.CreateDirectory(repository);
        bare.Cli.Must(repository, "init", "--quiet");
        var globalBefore = File.ReadAllText(bare.GlobalConfigPath);
        var localBefore = File.ReadAllText(Path.Combine(repository, ".git", "config"));

        var result = await client.GetIdentityAsync(repository);

        Assert.That(result.Value!.Name, Is.Null);
        Assert.That(result.Value.NameScope, Is.EqualTo(GitConfigScope.None));
        Assert.That(result.Value.Email, Is.Null);
        Assert.That(result.Value.IsComplete, Is.False);
        Assert.That(File.ReadAllText(bare.GlobalConfigPath), Is.EqualTo(globalBefore), "identity lookup is read-only");
        Assert.That(File.ReadAllText(Path.Combine(repository, ".git", "config")), Is.EqualTo(localBefore));
    }

    [Test]
    public async Task BranchExists_matches_local_branches_and_remote_tracking_short_names()
    {
        var remote = NewBareRemote();
        var origin = NewRepositoryWithCommit();
        Git.Must(origin, "remote", "add", "origin", remote);
        Git.Must(origin, "push", "--quiet", "-u", "origin", "main");

        var other = CloneRemote(remote, "other");
        Git.Must(other, "checkout", "--quiet", "-b", "remote-only");
        Git.Must(other, "push", "--quiet", "-u", "origin", "remote-only");
        Git.Must(origin, "fetch", "--quiet", "origin");

        Assert.That((await Client.BranchExistsAsync(origin, "main")).Value, Is.True);
        Assert.That((await Client.BranchExistsAsync(origin, "remote-only")).Value, Is.True,
            "a remote-tracking branch matches by the name after its remote");
        Assert.That((await Client.BranchExistsAsync(origin, "origin/remote-only")).Value, Is.True);
        Assert.That((await Client.BranchExistsAsync(origin, "does-not-exist")).Value, Is.False);
        Assert.That((await Client.BranchExistsAsync(origin, string.Empty)).Value, Is.False);
    }

    [Test]
    public async Task IsBranchNameValid_accepts_slashes_and_rejects_git_forbidden_names()
    {
        var repository = NewRepositoryWithCommit();

        Assert.That((await Client.IsBranchNameValidAsync(repository, "feature/login")).Value, Is.True);
        Assert.That((await Client.IsBranchNameValidAsync(repository, "bad..name")).Value, Is.False);
        Assert.That((await Client.IsBranchNameValidAsync(repository, "has space")).Value, Is.False);
        Assert.That((await Client.IsBranchNameValidAsync(repository, "ends.lock")).Value, Is.False);
        Assert.That((await Client.IsBranchNameValidAsync(repository, "   ")).Value, Is.False);
    }
}
