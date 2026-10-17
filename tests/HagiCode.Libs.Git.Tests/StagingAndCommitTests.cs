using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Tasks 2.3 and 2.4: staging, unstaging, the path-escape guard, and commit semantics.</summary>
[TestFixture]
public sealed class StagingAndCommitTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task Stage_then_commit_round_trips_through_status_and_log()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "feature.txt", "feature\n");

        var stage = await Client.StageAsync(repository, "feature.txt");
        var commit = await Client.CommitAsync(repository, "add feature");

        Assert.That(stage.Success, Is.True);
        Assert.That(commit.Success, Is.True, commit.Failure?.Message);
        Assert.That(commit.Value!.CommitSha, Is.EqualTo(Git.Must(repository, "rev-parse", "HEAD")));
        Assert.That((await Client.ProbeDirtyAsync(repository)).Value!.HasChanges, Is.False);
    }

    [Test]
    public async Task Stage_adds_a_file_and_unstage_removes_it_from_the_index_only()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "a.txt", "a\n");

        await Client.StageAsync(repository, "a.txt");
        var staged = (await Client.GetStatusAsync(repository)).Value!.Entries.Single();
        var unstage = await Client.UnstageAsync(repository, "a.txt");
        var after = (await Client.GetStatusAsync(repository)).Value!.Entries.Single();

        Assert.That(staged.IndexChange, Is.EqualTo(GitChangeKind.Added));
        Assert.That(unstage.Success, Is.True);
        Assert.That(after.IndexChange, Is.EqualTo(GitChangeKind.None));
        Assert.That(after.WorkingTreeChange, Is.EqualTo(GitChangeKind.Untracked));
        Assert.That(File.ReadAllText(Path.Combine(repository, "a.txt")), Is.EqualTo("a\n"), "the working tree is untouched");
    }

    [Test]
    public async Task Stage_records_a_deletion()
    {
        var repository = NewRepositoryWithCommit();
        File.Delete(Path.Combine(repository, "README.md"));

        var stage = await Client.StageAsync(repository, "README.md");
        var status = await Client.GetStatusAsync(repository);

        Assert.That(stage.Success, Is.True);
        Assert.That(status.Value!.Entries.Single().IndexChange, Is.EqualTo(GitChangeKind.Deleted));
    }

    [Test]
    public async Task Unstage_works_before_the_first_commit()
    {
        var repository = NewRepository();
        WriteFile(repository, "first.txt", "x\n");
        await Client.StageAsync(repository, "first.txt");

        var result = await Client.UnstageAsync(repository, "first.txt");
        var status = await Client.GetStatusAsync(repository);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(status.Value!.Entries.Single().IndexChange, Is.EqualTo(GitChangeKind.None));
    }

    [Test]
    public async Task StageAll_stages_every_change_and_reports_the_count()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "README.md", "changed\n");
        WriteFile(repository, "new one.txt", "x\n");
        WriteFile(repository, "中文/新.txt", "y\n");

        var result = await Client.StageAllAsync(repository);
        var status = await Client.GetStatusAsync(repository);

        Assert.That(result.Value, Is.EqualTo(3));
        Assert.That(status.Value!.Entries.All(entry => entry.IndexChange != GitChangeKind.None), Is.True);
    }

    [Test]
    public async Task UnstageAll_clears_the_index_and_reports_the_count()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "README.md", "changed\n");
        Git.Must(repository, "add", "README.md");

        var result = await Client.UnstageAllAsync(repository);
        var status = await Client.GetStatusAsync(repository);

        Assert.That(result.Value, Is.EqualTo(1));
        Assert.That(status.Value!.Entries.Single().IndexChange, Is.EqualTo(GitChangeKind.None));
        Assert.That(status.Value.Entries.Single().WorkingTreeChange, Is.EqualTo(GitChangeKind.Modified));
    }

    [Test]
    public async Task Stage_treats_glob_characters_in_a_name_literally()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "a[1].txt", "one\n");
        WriteFile(repository, "a1.txt", "two\n");

        await Client.StageAsync(repository, "a[1].txt");
        var status = await Client.GetStatusAsync(repository);

        var staged = status.Value!.Entries.Where(entry => entry.IndexChange != GitChangeKind.None).Select(entry => entry.Path).ToList();
        Assert.That(staged, Is.EqualTo(new[] { "a[1].txt" }),
            "the glob pattern a[1].txt must not also match a1.txt");
    }

    [Test]
    public async Task Stage_rejects_paths_outside_the_repository()
    {
        var repository = NewRepositoryWithCommit();
        var outside = Path.Combine(Environment.Root, "outside.txt");
        File.WriteAllText(outside, "x\n");

        var relativeEscape = await Client.StageAsync(repository, "../outside.txt");
        var absoluteOutside = await Client.StageAsync(repository, outside);

        Assert.That(relativeEscape.Failure!.Code, Is.EqualTo(GitFailureCode.PathOutsideRepository));
        Assert.That(absoluteOutside.Failure!.Code, Is.EqualTo(GitFailureCode.PathOutsideRepository));
        Assert.That(File.Exists(outside), Is.True, "rejection must not touch the file");
    }

    [Test]
    public async Task Stage_accepts_an_absolute_path_inside_the_repository()
    {
        var repository = NewRepositoryWithCommit();
        var inside = WriteFile(repository, "inside.txt", "x\n");

        var result = await Client.StageAsync(repository, inside);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That((await Client.GetStatusAsync(repository)).Value!.Entries.Single().IndexChange, Is.EqualTo(GitChangeKind.Added));
    }

    [Test]
    public async Task Stage_rejects_empty_paths_and_the_repository_root()
    {
        var repository = NewRepositoryWithCommit();

        Assert.That((await Client.StageAsync(repository, string.Empty)).Failure!.Code, Is.EqualTo(GitFailureCode.InvalidArgument));
        Assert.That((await Client.StageAsync(repository, ".")).Failure!.Code, Is.EqualTo(GitFailureCode.InvalidArgument));
    }

    [Test]
    public async Task Commit_records_a_multi_line_non_ascii_message_unchanged()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "note.txt", "x\n");
        await Client.StageAsync(repository, "note.txt");
        const string message = "Feature: 中文标题\n\nBody line one.\n\n  indented line\n# not a comment\n";

        var result = await Client.CommitAsync(repository, message);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value!.Message, Is.EqualTo(message));
        var recorded = Git.Must(repository, "log", "-1", "--format=%B");
        Assert.That(recorded.TrimEnd(), Is.EqualTo(message.TrimEnd()), "the commit must keep the text exactly, including the # line");
    }

    [Test]
    public async Task Commit_with_nothing_staged_fails_and_creates_no_commit()
    {
        var repository = NewRepositoryWithCommit();
        var headBefore = Git.Must(repository, "rev-parse", "HEAD");

        var result = await Client.CommitAsync(repository, "empty");

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.NothingStaged));
        Assert.That(Git.Must(repository, "rev-parse", "HEAD"), Is.EqualTo(headBefore));
    }

    [Test]
    public async Task Commit_without_an_identity_fails_and_writes_no_configuration()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "note.txt", "x\n");
        await Client.StageAsync(repository, "note.txt");

        using var bare = new TestGitEnvironment(withIdentity: false);
        var client = CreateClient(bare.Variables);
        var globalBefore = File.ReadAllText(bare.GlobalConfigPath);
        var localBefore = File.ReadAllText(Path.Combine(repository, ".git", "config"));

        var result = await client.CommitAsync(repository, "no identity");

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.IdentityNotConfigured));
        Assert.That(File.ReadAllText(bare.GlobalConfigPath), Is.EqualTo(globalBefore), "the wrapper must not write global config");
        Assert.That(File.ReadAllText(Path.Combine(repository, ".git", "config")), Is.EqualTo(localBefore), "nor local config");
    }

    [Test]
    public async Task Commit_on_an_unborn_branch_creates_the_first_commit()
    {
        var repository = NewRepository();
        WriteFile(repository, "first.txt", "x\n");
        await Client.StageAsync(repository, "first.txt");

        var result = await Client.CommitAsync(repository, "first");

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(Git.Must(repository, "log", "-1", "--format=%s"), Is.EqualTo("first"));
    }
}
