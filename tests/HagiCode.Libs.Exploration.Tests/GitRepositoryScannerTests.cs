using FluentAssertions;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Exploration.Git;

namespace HagiCode.Libs.Exploration.Tests;

public sealed class GitRepositoryScannerTests
{
    [Fact]
    public async Task ScanAsync_discovers_repositories_and_skips_excluded_directories()
    {
        using var sandbox = new GitSandbox();
        sandbox.CreateRepository("repo-a");
        sandbox.CreateRepository(Path.Combine("node_modules", "skip-me"));
        sandbox.CreateRepository(Path.Combine("nested", "repo-b"));

        var scanner = new GitRepositoryScanner(new CliProcessManager(), new CliExecutableResolver());
        var repositories = await scanner.ScanAsync(sandbox.RootPath, excludedDirectories: ["node_modules"]);

        repositories.Select(static repository => Path.GetFileName(repository.RepositoryPath)).Should().BeEquivalentTo(["repo-a", "repo-b"]);
    }

    [Fact]
    public async Task ScanAsync_respects_max_depth()
    {
        using var sandbox = new GitSandbox();
        sandbox.CreateRepository(Path.Combine("level1", "level2", "repo-c"));

        var scanner = new GitRepositoryScanner(new CliProcessManager(), new CliExecutableResolver());
        var repositories = await scanner.ScanAsync(sandbox.RootPath, maxDepth: 1);

        repositories.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRepositoryInfoAsync_reports_branch_remote_and_worktree_state()
    {
        using var sandbox = new GitSandbox();
        var repositoryPath = sandbox.CreateRepository("repo-state");
        sandbox.RunGit(repositoryPath, "remote", "add", "origin", "https://example.com/test.git");
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), "hello");

        var scanner = new GitRepositoryScanner(new CliProcessManager(), new CliExecutableResolver());
        var info = await scanner.GetRepositoryInfoAsync(repositoryPath);

        info.RepositoryPath.Should().Be(Path.GetFullPath(repositoryPath));
        info.Branch.Should().NotBeNullOrWhiteSpace();
        info.RemoteUrl.Should().Be("https://example.com/test.git");
        info.HasUncommittedChanges.Should().BeTrue();
    }

    private sealed class GitSandbox : IDisposable
    {
        public GitSandbox()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"hagicode-libs-git-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string CreateRepository(string relativePath)
        {
            var repositoryPath = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(repositoryPath);
            RunGit(repositoryPath, "init");
            return repositoryPath;
        }

        public void RunGit(string workingDirectory, params string[] args)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(startInfo)!;
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException(error);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, true);
            }
        }
    }
}
