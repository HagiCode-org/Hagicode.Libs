namespace HagiCode.Libs.Git.Tests.Support;

/// <summary>
/// A throw-away directory with a private global git configuration. Every git process started by a test sees only this
/// configuration: no system config, no developer identity, no credential helpers, and a HOME inside the temp root.
/// </summary>
public sealed class TestGitEnvironment : IDisposable
{
    public const string TestName = "Hagicode Test";
    public const string TestEmail = "hagicode-test@example.invalid";

    /// <summary>Creates the environment. <paramref name="withIdentity" /> = false writes a global file with no user identity.</summary>
    public TestGitEnvironment(bool withIdentity = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "hagicode-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "home"));
        Directory.CreateDirectory(Path.Combine(Root, "xdg"));

        var identity = withIdentity
            ? $"[user]\n\tname = {TestName}\n\temail = {TestEmail}\n"
            : string.Empty;
        File.WriteAllText(GlobalConfigPath, "[init]\n\tdefaultBranch = main\n" + identity);

        Variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_GLOBAL"] = GlobalConfigPath,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["HOME"] = Path.Combine(Root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(Root, "xdg"),
            ["GIT_AUTHOR_NAME"] = null,
            ["GIT_AUTHOR_EMAIL"] = null,
            ["GIT_COMMITTER_NAME"] = null,
            ["GIT_COMMITTER_EMAIL"] = null
        };

        Cli = new GitCli(Variables);
    }

    /// <summary>The temp root that holds every file of this environment.</summary>
    public string Root { get; }

    /// <summary>The path of the isolated global configuration file.</summary>
    public string GlobalConfigPath => Path.Combine(Root, "gitconfig");

    /// <summary>Environment overrides applied to every child process.</summary>
    public IReadOnlyDictionary<string, string?> Variables { get; }

    /// <summary>Direct git access for fixture setup and verification.</summary>
    public GitCli Cli { get; }

    public void Dispose()
    {
        DeleteTree(Root);
    }

    /// <summary>Deletes a tree, clearing read-only bits first (git object files are read-only on Windows).</summary>
    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
