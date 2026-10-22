using System.Text.Json;
using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests;

/// <summary>
/// The git processes each operation starts, read from git's own trace (GIT_TRACE2_EVENT), not from a test seam.
/// This keeps the backend workload intent testable without exposing internals: a status read runs only the dirty probe,
/// and the full status runs one porcelain process.
/// </summary>
[TestFixture]
public sealed class ProcessWorkloadTests : GitTestBase
{
    private string _traceFile = null!;
    private IGitClient _traced = null!;

    [SetUp]
    public void SetUpTrace()
    {
        _traceFile = Path.Combine(Environment.Root, "trace.json");
        var variables = new Dictionary<string, string?>(Environment.Variables, StringComparer.Ordinal)
        {
            ["GIT_TRACE2_EVENT"] = _traceFile
        };
        _traced = new GitClient(new GitClientOptions { EnvironmentVariables = variables });
    }

    [TestCase("clean", new[] { "status" })]
    [TestCase("staged-only", new[] { "status" })]
    [TestCase("unstaged-only", new[] { "status" })]
    [TestCase("untracked-only", new[] { "status" })]
    public async Task ProbeDirty_runs_one_status_process_whatever_the_state(string shape, string[] expected)
    {
        var repository = NewRepositoryWithCommit();
        if (shape == "staged-only")
        {
            WriteFile(repository, "README.md", "changed\n");
            Git.Must(repository, "add", "README.md");
        }
        else if (shape == "unstaged-only")
        {
            WriteFile(repository, "README.md", "changed\n");
        }
        else if (shape == "untracked-only")
        {
            WriteFile(repository, "new.txt", "x\n");
        }

        var commands = await Trace(() => _traced.ProbeDirtyAsync(repository));

        Assert.That(commands, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetStatus_runs_exactly_one_porcelain_process_whether_clean_or_dirty()
    {
        var repository = NewRepositoryWithCommit();
        var clean = await Trace(() => _traced.GetStatusAsync(repository));
        WriteFile(repository, "new.txt", "x\n");
        var dirty = await Trace(() => _traced.GetStatusAsync(repository));

        Assert.That(clean, Is.EqualTo(new[] { "status" }));
        Assert.That(dirty, Is.EqualTo(new[] { "status" }));
    }

    [Test]
    public async Task StageAll_enumerates_once_then_stages_in_a_single_command()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "a.txt", "a\n");
        WriteFile(repository, "b.txt", "b\n");

        var commands = await Trace(() => _traced.StageAllAsync(repository));

        Assert.That(commands, Is.EqualTo(new[] { "status", "add" }));
    }

    [Test]
    public async Task Commit_reads_the_staged_state_and_identity_then_commits_once()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "c.txt", "c\n");
        Git.Must(repository, "add", "c.txt");

        var commands = await Trace(() => _traced.CommitAsync(repository, "one commit"));

        Assert.That(commands, Is.EqualTo(new[] { "diff", "config", "commit", "rev-parse" }));
    }

    /// <summary>Runs the action and returns the git subcommand of each process it started, in order.</summary>
    private async Task<List<string>> Trace(Func<Task> action)
    {
        if (File.Exists(_traceFile))
        {
            File.Delete(_traceFile);
        }

        await action();

        var commands = new List<string>();
        foreach (var line in File.ReadLines(_traceFile))
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("event", out var kind) || kind.GetString() != "start")
            {
                continue;
            }

            var argv = document.RootElement.GetProperty("argv").EnumerateArray().Select(item => item.GetString()!).ToList();
            var command = SubcommandOf(argv);

            // git starts "maintenance run --auto" itself after a commit. It is not part of the wrapper's work.
            if (command != "maintenance")
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    /// <summary>The subcommand after git's global options (<c>-c</c> pairs and <c>--no-optional-locks</c>).</summary>
    private static string SubcommandOf(List<string> argv)
    {
        var index = 1;
        while (index < argv.Count)
        {
            if (argv[index] == "-c")
            {
                index += 2;
            }
            else if (argv[index].StartsWith("--", StringComparison.Ordinal) && argv[index] is "--no-optional-locks" or "--literal-pathspecs")
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return index < argv.Count ? argv[index] : string.Empty;
    }
}
