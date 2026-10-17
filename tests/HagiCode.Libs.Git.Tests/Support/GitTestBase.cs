using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests;

/// <summary>
/// Base for every git test: a hermetic environment, a client built from it, and helpers that create repositories and remotes.
/// Every derived test is an integration test (real git), so the class carries the Integration tier.
/// </summary>
[Category(TestTiers.Integration)]
public abstract class GitTestBase
{
    private static readonly Lazy<bool> GitInstalled = new(ProbeGit);

    private readonly List<string> _extraRoots = [];

    protected TestGitEnvironment Environment { get; private set; } = null!;

    protected IGitClient Client { get; private set; } = null!;

    protected GitCli Git => Environment.Cli;

    [SetUp]
    public void SetUpGitEnvironment()
    {
        RequireGit();
        Environment = new TestGitEnvironment();
        Client = CreateClient(Environment.Variables);
    }

    [TearDown]
    public void TearDownGitEnvironment()
    {
        foreach (var root in _extraRoots)
        {
            TestGitEnvironment.DeleteTree(root);
        }

        Environment?.Dispose();
    }

    /// <summary>Creates the client under test. Override to wrap it, for example in a timing decorator.</summary>
    protected virtual IGitClient CreateClient(IReadOnlyDictionary<string, string?> variables)
    {
        return new Timing.TimedGitClient(new GitClient(new GitClientOptions { EnvironmentVariables = variables }));
    }

    /// <summary>Creates a fresh repository on the <c>main</c> branch, with no commits, inside the test environment.</summary>
    protected string NewRepository(string name = "repo")
    {
        var path = Path.Combine(Environment.Root, name);
        Directory.CreateDirectory(path);
        Git.Must(path, "init", "--quiet");
        return path;
    }

    /// <summary>Creates a repository with one commit containing <c>README.md</c>.</summary>
    protected string NewRepositoryWithCommit(string name = "repo")
    {
        var path = NewRepository(name);
        WriteFile(path, "README.md", "hello\n");
        Git.Must(path, "add", "README.md");
        Git.Must(path, "commit", "--quiet", "-m", "initial");
        return path;
    }

    /// <summary>Creates an empty bare repository that acts as a remote.</summary>
    protected string NewBareRemote(string name = "remote.git")
    {
        var path = Path.Combine(Environment.Root, name);
        Git.Must(Environment.Root, "init", "--quiet", "--bare", path);
        return path;
    }

    /// <summary>Clones <paramref name="remote" /> into a new directory with the given name. Used to create a second collaborator.</summary>
    protected string CloneRemote(string remote, string name)
    {
        var path = Path.Combine(Environment.Root, name);
        Git.Must(Environment.Root, "clone", "--quiet", remote, path);
        return path;
    }

    /// <summary>Writes a file under <paramref name="repository" />, creating folders as needed. Content is written as UTF-8 without BOM.</summary>
    protected static string WriteFile(string repository, string relativePath, string content)
    {
        var full = Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
        return full;
    }

    /// <summary>Registers an extra directory tree for deletion at teardown.</summary>
    protected void TrackRoot(string path) => _extraRoots.Add(path);

    /// <summary>
    /// Skips the test locally when git is not installed, and fails it on CI, where git is a hard requirement.
    /// </summary>
    protected static void RequireGit()
    {
        if (GitInstalled.Value)
        {
            return;
        }

        var message = "git is not installed or not on PATH; the git integration tests need it.";
        if (IsCi())
        {
            Assert.Fail(message);
        }

        Assert.Ignore(message);
    }

    private static bool IsCi()
    {
        return string.Equals(System.Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProbeGit()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "hagicode-git-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var result = new GitCli(new Dictionary<string, string?>()).Run(root, "--version");
                return result.ExitCode == 0;
            }
            finally
            {
                TestGitEnvironment.DeleteTree(root);
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
