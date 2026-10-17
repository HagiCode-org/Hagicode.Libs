using HagiCode.Libs.Git.Internal;
using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 1.2: git discovery on the host, and the failure that names what was searched.</summary>
[TestFixture]
public sealed class ExecutableLocatorTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public void Resolve_finds_git_on_the_current_path()
    {
        var resolution = new GitExecutableLocator().Resolve(explicitPath: null, environmentVariables: null);

        Assert.That(resolution.Path, Is.Not.Null, resolution.Message);
        Assert.That(File.Exists(resolution.Path), Is.True);
        Assert.That(Path.GetFileNameWithoutExtension(resolution.Path), Is.EqualTo("git").IgnoreCase);
    }

    [Test]
    public void Resolve_reports_a_missing_git_and_names_every_searched_directory()
    {
        var emptyDirectory = Path.Combine(Environment.Root, "empty-bin");
        Directory.CreateDirectory(emptyDirectory);
        var searched = new Dictionary<string, string?>(Environment.Variables, StringComparer.Ordinal)
        {
            ["PATH"] = emptyDirectory
        };

        var resolution = new GitExecutableLocator().Resolve(explicitPath: null, environmentVariables: searched);

        Assert.That(resolution.Path, Is.Null);
        Assert.That(resolution.Message, Does.Contain("No 'git' executable was found"));
        Assert.That(resolution.Message, Does.Contain(emptyDirectory),
            "the failure must name the directories that were searched");
    }

    [Test]
    public void Resolve_uses_an_explicit_path_and_reports_when_it_is_missing()
    {
        var missing = Path.Combine(Environment.Root, "bin", "git-not-here");

        var resolution = new GitExecutableLocator().Resolve(missing, environmentVariables: null);

        Assert.That(resolution.Path, Is.Null);
        Assert.That(resolution.Message, Does.Contain(missing));
    }

    [Test]
    public async Task Client_returns_git_unavailable_when_git_is_not_on_path()
    {
        var emptyDirectory = Path.Combine(Environment.Root, "no-git-here");
        Directory.CreateDirectory(emptyDirectory);
        var repository = NewRepositoryWithCommit();
        var client = new GitClient(new GitClientOptions
        {
            EnvironmentVariables = new Dictionary<string, string?>(Environment.Variables, StringComparer.Ordinal)
            {
                ["PATH"] = emptyDirectory
            }
        });

        var result = await client.GetStatusAsync(repository);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.GitUnavailable));
        Assert.That(result.Failure.Message, Does.Contain(emptyDirectory));
    }
}
