using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 1.3: the dirty probe starts exactly one git process whatever the repository state.</summary>
[TestFixture]
public sealed class ProbeDirtyProcessCountTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task ProbeDirty_starts_one_process_for_a_clean_repository()
    {
        var repository = NewRepositoryWithCommit();

        await AssertOneProcessAsync(repository, expectedDirty: false);
    }

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task ProbeDirty_starts_one_process_for_staged_only_changes()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "staged.txt", "staged\n");
        Git.Must(repository, "add", "staged.txt");

        await AssertOneProcessAsync(repository, expectedDirty: true);
    }

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task ProbeDirty_starts_one_process_for_unstaged_only_changes()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "README.md", "changed\n");

        await AssertOneProcessAsync(repository, expectedDirty: true);
    }

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task ProbeDirty_starts_one_process_for_untracked_only_changes()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "untracked.txt", "new\n");

        await AssertOneProcessAsync(repository, expectedDirty: true);
    }

    private async Task AssertOneProcessAsync(string repository, bool expectedDirty)
    {
        var runner = new GitProcessRunner(Environment.Variables);
        var counting = new GitClient(new GitClientOptions { EnvironmentVariables = Environment.Variables }, resolver: null, runner);

        var probe = await counting.ProbeDirtyAsync(repository);

        Assert.That(probe.Success, Is.True, probe.Failure?.Message);
        Assert.That(probe.Value!.HasChanges, Is.EqualTo(expectedDirty));
        Assert.That(runner.StartedCount, Is.EqualTo(1), "the dirty probe must start exactly one git process");
    }
}
