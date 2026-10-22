namespace HagiCode.Libs.Git.Tests;

/// <summary>
/// Task 1.4: the single-process boolean agrees with separate git checks for the states where a status-based answer could
/// drift from the diff-based one. The separate checks are the reference: staged uses <c>diff --cached</c> so it also works
/// on an unborn HEAD, where <c>diff-index HEAD</c> fails.
/// </summary>
[TestFixture]
public sealed class ProbeDirtyParityTests : GitTestBase
{
    [Test]
    public async Task Parity_for_an_untracked_file_in_a_nested_directory()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "src/deep/inner/new.txt", "new\n");

        await AssertParityAsync(repository, expectedDirty: true);
    }

    [Test]
    public async Task Parity_for_an_ignored_file()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, ".gitignore", "*.log\n");
        Git.Must(repository, "add", ".gitignore");
        Git.Must(repository, "commit", "--quiet", "-m", "ignore rules");
        WriteFile(repository, "debug.log", "noise\n");

        await AssertParityAsync(repository, expectedDirty: false);
    }

    [Test]
    public async Task Parity_for_a_modified_submodule()
    {
        var repository = NewRepositoryWithCommit();
        var submoduleSource = NewRepositoryWithCommit("submodule-source");
        Git.Must(repository, "-c", "protocol.file.allow=always", "submodule", "add", submoduleSource, "sub");
        Git.Must(repository, "commit", "--quiet", "-m", "add submodule");
        await AssertParityAsync(repository, expectedDirty: false);

        var submodule = Path.Combine(repository, "sub");
        WriteFile(submodule, "moved.txt", "x\n");
        Git.Must(submodule, "add", "moved.txt");
        Git.Must(submodule, "commit", "--quiet", "-m", "move submodule HEAD");

        await AssertParityAsync(repository, expectedDirty: true);
    }

    [Test]
    public async Task Parity_for_an_intent_to_add_file()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "intent.txt", "x\n");
        Git.Must(repository, "add", "--intent-to-add", "intent.txt");

        await AssertParityAsync(repository, expectedDirty: true);
    }

    [Test]
    public async Task Parity_for_a_repository_with_no_commits_and_one_staged_file()
    {
        var repository = NewRepository();
        WriteFile(repository, "staged.txt", "x\n");
        Git.Must(repository, "add", "staged.txt");

        await AssertParityAsync(repository, expectedDirty: true);
    }

    [Test]
    public async Task Parity_for_a_detached_head_clean_and_then_with_an_untracked_file()
    {
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "checkout", "--quiet", "--detach");

        await AssertParityAsync(repository, expectedDirty: false);

        WriteFile(repository, "detached-untracked.txt", "x\n");

        await AssertParityAsync(repository, expectedDirty: true);
    }

    private async Task AssertParityAsync(string repository, bool expectedDirty)
    {
        var probe = await Client.ProbeDirtyAsync(repository);
        var separate = SeparateChecksReportChanges(repository);

        Assert.That(probe.Success, Is.True, probe.Failure?.Message);
        Assert.That(probe.Value!.HasChanges, Is.EqualTo(separate), "the single status read must agree with the separate checks");
        Assert.That(probe.Value.HasChanges, Is.EqualTo(expectedDirty));
    }

    /// <summary>Staged, unstaged and untracked checks, each a separate git process, ORed together as the old probe did.</summary>
    private bool SeparateChecksReportChanges(string repository)
    {
        var staged = Git.Run(repository, "diff", "--cached", "--name-only");
        var unstaged = Git.Run(repository, "diff-files", "--name-only");
        var untracked = Git.Run(repository, "ls-files", "--others", "--exclude-standard");

        return new[] { staged, unstaged, untracked }.Any(result =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
            return result.StandardOutput.Trim().Length > 0;
        });
    }
}
