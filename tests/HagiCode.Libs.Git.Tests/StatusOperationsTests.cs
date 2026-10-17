namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 2.2: the dirty probe (boolean and branch only) and full status, against real working trees.</summary>
[TestFixture]
public sealed class StatusOperationsTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task ProbeDirty_is_clean_then_dirty_after_an_untracked_file()
    {
        var repository = NewRepositoryWithCommit();

        var clean = await Client.ProbeDirtyAsync(repository);
        WriteFile(repository, "new.txt", "new\n");
        var dirty = await Client.ProbeDirtyAsync(repository);

        Assert.That(clean.Value!.HasChanges, Is.False);
        Assert.That(clean.Value.Branch, Is.EqualTo("main"));
        Assert.That(clean.Value.IsConclusive, Is.True);
        Assert.That(dirty.Value!.HasChanges, Is.True);
    }

    [Test]
    public async Task ProbeDirty_sees_staged_and_unstaged_changes()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "README.md", "changed\n");

        var unstaged = await Client.ProbeDirtyAsync(repository);
        Git.Must(repository, "add", "README.md");
        var staged = await Client.ProbeDirtyAsync(repository);

        Assert.That(unstaged.Value!.HasChanges, Is.True);
        Assert.That(staged.Value!.HasChanges, Is.True);
    }

    [Test]
    public async Task ProbeDirty_ignores_paths_matched_by_ignore_rules()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, ".gitignore", "*.log\n");
        Git.Must(repository, "add", ".gitignore");
        Git.Must(repository, "commit", "--quiet", "-m", "ignore rules");
        WriteFile(repository, "debug.log", "noise\n");

        var probe = await Client.ProbeDirtyAsync(repository);

        Assert.That(probe.Value!.HasChanges, Is.False, "git's ignore rules are authoritative, so an ignored file is not a change");
    }

    [Test]
    public async Task ProbeDirty_handles_a_repository_with_no_commits()
    {
        var repository = NewRepository();
        var empty = await Client.ProbeDirtyAsync(repository);

        WriteFile(repository, "staged.txt", "x\n");
        Git.Must(repository, "add", "staged.txt");
        var staged = await Client.ProbeDirtyAsync(repository);

        Assert.That(empty.Success, Is.True, "an unborn HEAD is not a failure");
        Assert.That(empty.Value!.HasChanges, Is.False);
        Assert.That(staged.Value!.HasChanges, Is.True, "staged content on an unborn branch is a change");
        Assert.That(staged.Value.Branch, Is.EqualTo("main"));
    }

    [Test]
    public async Task GetStatus_lists_staged_unstaged_and_untracked_paths_by_category()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "staged.txt", "staged\n");
        Git.Must(repository, "add", "staged.txt");
        WriteFile(repository, "README.md", "modified\n");
        WriteFile(repository, "untracked.txt", "new\n");

        var status = await Client.GetStatusAsync(repository);

        Assert.That(status.Success, Is.True);
        Assert.That(status.Value!.Branch, Is.EqualTo("main"));
        var staged = status.Value.Entries.Single(entry => entry.Path == "staged.txt");
        Assert.That(staged.IndexChange, Is.EqualTo(GitChangeKind.Added));
        Assert.That(staged.IsStaged, Is.True);
        var modified = status.Value.Entries.Single(entry => entry.Path == "README.md");
        Assert.That(modified.WorkingTreeChange, Is.EqualTo(GitChangeKind.Modified));
        Assert.That(modified.IsUnstaged, Is.True);
        var untracked = status.Value.Entries.Single(entry => entry.Path == "untracked.txt");
        Assert.That(untracked.WorkingTreeChange, Is.EqualTo(GitChangeKind.Untracked));
        Assert.That(untracked.IsStaged, Is.False);
    }

    [Test]
    public async Task GetStatus_omits_ignored_files()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, ".gitignore", "build/\n");
        WriteFile(repository, "build/output.bin", "x\n");

        var status = await Client.GetStatusAsync(repository);

        Assert.That(status.Value!.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { ".gitignore" }));
    }

    [Test]
    public async Task GetStatus_reports_a_staged_rename_with_its_original_path()
    {
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "mv", "README.md", "GUIDE.md");

        var status = await Client.GetStatusAsync(repository);

        var rename = status.Value!.Entries.Single();
        Assert.That(rename.Path, Is.EqualTo("GUIDE.md"));
        Assert.That(rename.OriginalPath, Is.EqualTo("README.md"));
        Assert.That(rename.IndexChange, Is.EqualTo(GitChangeKind.Renamed));
    }

    [Test]
    public async Task GetStatus_reports_a_merge_conflict_as_unmerged_on_both_sides()
    {
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "checkout", "--quiet", "-b", "side");
        WriteFile(repository, "README.md", "side change\n");
        Git.Must(repository, "commit", "--quiet", "-am", "side");
        Git.Must(repository, "checkout", "--quiet", "main");
        WriteFile(repository, "README.md", "main change\n");
        Git.Must(repository, "commit", "--quiet", "-am", "main");
        var merge = Git.Run(repository, "merge", "--no-edit", "side");
        Assert.That(merge.ExitCode, Is.Not.EqualTo(0), "the merge must conflict for this test to mean anything");

        var status = await Client.GetStatusAsync(repository);

        var conflict = status.Value!.Entries.Single(entry => entry.Path == "README.md");
        Assert.That(conflict.IndexChange, Is.EqualTo(GitChangeKind.Unmerged));
        Assert.That(conflict.WorkingTreeChange, Is.EqualTo(GitChangeKind.Unmerged));
    }

    [Test]
    public async Task GetStatus_keeps_non_ascii_and_spaced_names_unmangled()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "docs with space/中文 文件.md", "x\n");

        var status = await Client.GetStatusAsync(repository);

        Assert.That(status.Value!.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { "docs with space/中文 文件.md" }));
    }

    [Test]
    public async Task GetStatus_works_before_the_first_commit()
    {
        var repository = NewRepository();
        WriteFile(repository, "first.txt", "x\n");

        var status = await Client.GetStatusAsync(repository);

        Assert.That(status.Success, Is.True);
        Assert.That(status.Value!.Branch, Is.EqualTo("main"));
        Assert.That(status.Value.Entries.Single().WorkingTreeChange, Is.EqualTo(GitChangeKind.Untracked));
    }

    [Test]
    public async Task GetStatus_fails_for_a_path_that_is_not_a_repository()
    {
        var result = await Client.GetStatusAsync(Environment.Root);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
    }
}
