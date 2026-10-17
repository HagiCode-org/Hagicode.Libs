using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>
/// Task 3.3: dedicated, repeated scenarios on the generated repository. They are <c>Slow</c>, so they run in the full profile
/// (for example with <c>-p:TestProfile=full</c> and <c>HAGICODE_LIBS_GIT_TIMING_DIR</c> set), and every wrapper operation is
/// measured across the three scenarios: <c>read-path</c>, <c>write-path</c> and <c>clone</c>.
/// </summary>
[TestFixture]
[Category(TestTiers.Slow)]
public sealed class TimingScenarioTests : GitTestBase
{
    private const string ReadPath = "read-path";
    private const string WritePath = "write-path";
    private const string Clone = "clone";

    [Test]
    public async Task Read_path_scenario_measures_every_read_operation_on_a_fixed_repository()
    {
        var clean = GeneratedRepository.Create(Git, Path.Combine(Environment.Root, "read-clean"), TestGitEnvironment.TestEmail);
        var dirty = GeneratedRepository.Create(Git, Path.Combine(Environment.Root, "read-dirty"), TestGitEnvironment.TestEmail);
        Git.Must(clean, "remote", "add", "origin", "https://example.invalid/generated.git");
        MakeDirty(dirty);
        var scanRoot = Path.Combine(Environment.Root, "scan-root");
        Directory.CreateDirectory(Path.Combine(scanRoot, "one", ".git"));
        Directory.CreateDirectory(Path.Combine(scanRoot, "two", ".git"));

        await ScenarioRunner.RunAsync(ReadPath, async _ =>
        {
            await Client.IsRepositoryAsync(clean);
            await Client.FindRepositoryRootAsync(Path.Combine(clean, "folder00"));
            await Client.GetRemoteUrlAsync(clean);
            await Client.GetIdentityAsync(clean);
            await Client.GetCurrentBranchAsync(clean);
            await Client.BranchExistsAsync(clean, "main");
            await Client.IsBranchNameValidAsync(clean, "feature/read-path");
            await Client.ProbeDirtyAsync(clean);
            await Client.ProbeDirtyAsync(dirty);
            await Client.GetStatusAsync(clean);
            await Client.GetStatusAsync(dirty);
            await Client.ScanForRepositoriesAsync(scanRoot);
        });

        var summaries = TimingStatistics.Summarize(DurationRecorder.Shared.Snapshot())
            .Where(summary => summary.Scenario == ReadPath)
            .Select(summary => summary.Operation)
            .ToHashSet(StringComparer.Ordinal);
        Assert.That(summaries, Is.SupersetOf(new[]
        {
            nameof(IGitClient.IsRepositoryAsync),
            nameof(IGitClient.FindRepositoryRootAsync),
            nameof(IGitClient.GetRemoteUrlAsync),
            nameof(IGitClient.GetIdentityAsync),
            nameof(IGitClient.GetCurrentBranchAsync),
            nameof(IGitClient.BranchExistsAsync),
            nameof(IGitClient.IsBranchNameValidAsync),
            nameof(IGitClient.ProbeDirtyAsync),
            nameof(IGitClient.GetStatusAsync),
            nameof(IGitClient.ScanForRepositoriesAsync)
        }));
    }

    [Test]
    public async Task Write_path_scenario_measures_stage_commit_push_pull_and_rebase()
    {
        var remote = NewBareRemote();
        var repository = GeneratedRepository.Create(Git, Path.Combine(Environment.Root, "write-path"), TestGitEnvironment.TestEmail);
        Git.Must(repository, "remote", "add", "origin", remote);
        Git.Must(repository, "push", "--quiet", "-u", "origin", "main");
        var collaborator = CloneRemote(remote, "collaborator");

        var iteration = 0;
        await ScenarioRunner.RunAsync(WritePath, async index =>
        {
            iteration = index;
            var single = $"bench/one-{index}.txt";
            WriteFile(repository, single, "one\n");
            await Client.StageAsync(repository, single);
            await Client.UnstageAsync(repository, single);
            File.Delete(Path.Combine(repository, single.Replace('/', Path.DirectorySeparatorChar)));

            for (var file = 0; file < 10; file++)
            {
                WriteFile(repository, $"bench/all-{index}-{file}.txt", "many\n");
            }
            await Client.StageAllAsync(repository);
            await Client.UnstageAllAsync(repository);
            Directory.Delete(Path.Combine(repository, "bench"), recursive: true);

            WriteFile(repository, $"commits/{index}.txt", "commit\n");
            Git.Must(repository, "add", $"commits/{index}.txt");
            await Client.CommitAsync(repository, $"write-path commit {index}");
            await Client.PushAsync(repository);

            Git.Must(collaborator, "pull", "--quiet", "--rebase");
            WriteFile(collaborator, $"theirs/{index}.txt", "theirs\n");
            Git.Must(collaborator, "add", $"theirs/{index}.txt");
            Git.Must(collaborator, "commit", "--quiet", "-m", $"collaborator {index}");
            Git.Must(collaborator, "push", "--quiet");
            await Client.PullAsync(repository);

            WriteFile(collaborator, $"theirs/rebase-{index}.txt", "theirs\n");
            Git.Must(collaborator, "add", $"theirs/rebase-{index}.txt");
            Git.Must(collaborator, "commit", "--quiet", "-m", $"collaborator rebase {index}");
            Git.Must(collaborator, "push", "--quiet");
            WriteFile(repository, $"local/{index}.txt", "local\n");
            Git.Must(repository, "add", $"local/{index}.txt");
            Git.Must(repository, "commit", "--quiet", "-m", $"local {index}");
            await Client.RebaseAsync(repository);
        });

        Assert.That(iteration, Is.EqualTo(ScenarioRunner.WarmUpIterations + ScenarioRunner.MeasuredIterations - 1));
        var operations = TimingStatistics.Summarize(DurationRecorder.Shared.Snapshot())
            .Where(summary => summary.Scenario == WritePath)
            .Select(summary => summary.Operation)
            .ToHashSet(StringComparer.Ordinal);
        Assert.That(operations, Is.SupersetOf(new[]
        {
            nameof(IGitClient.StageAsync),
            nameof(IGitClient.UnstageAsync),
            nameof(IGitClient.StageAllAsync),
            nameof(IGitClient.UnstageAllAsync),
            nameof(IGitClient.CommitAsync),
            nameof(IGitClient.PushAsync),
            nameof(IGitClient.PullAsync),
            nameof(IGitClient.RebaseAsync)
        }));
    }

    [Test]
    public async Task Clone_scenario_clones_the_generated_repository_each_iteration()
    {
        var remote = NewBareRemote();
        var seed = GeneratedRepository.Create(Git, Path.Combine(Environment.Root, "clone-seed"), TestGitEnvironment.TestEmail);
        Git.Must(seed, "remote", "add", "origin", remote);
        Git.Must(seed, "push", "--quiet", "-u", "origin", "main");

        await ScenarioRunner.RunAsync(Clone, async index =>
        {
            var target = Path.Combine(Environment.Root, "clones", $"clone-{index}");
            var result = await Client.EnsureCloneAsync(remote, target);
            Assert.That(result.Success, Is.True, result.Failure?.Message);
        });

        var summary = TimingStatistics.Summarize(DurationRecorder.Shared.Snapshot())
            .Single(item => item.Scenario == Clone && item.Operation == nameof(IGitClient.EnsureCloneAsync));
        Assert.That(summary.Count, Is.EqualTo(ScenarioRunner.MeasuredIterations));
        Assert.That(summary.Failures, Is.EqualTo(0));
    }

    private static void MakeDirty(string repository)
    {
        for (var index = 0; index < 100; index++)
        {
            var path = Path.Combine(repository, GeneratedRepository.FileName(index));
            File.AppendAllText(path, "changed\n");
            File.WriteAllText(Path.Combine(repository, $"untracked-{index}.txt"), "new\n");
        }
    }
}
