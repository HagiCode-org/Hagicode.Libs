using System.Diagnostics;
using System.Runtime.InteropServices;
using HagiCode.Libs.Git.Internal;
using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 1.3: the process runner's contract, exercised against the real git executable.</summary>
[TestFixture]
public sealed class ProcessRunnerTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task Run_returns_exit_code_and_stdout_for_a_successful_command()
    {
        var outcome = await RunGitAsync(Environment.Root, TimeSpan.FromSeconds(30), ["--version"]);

        Assert.That(outcome.ExitCode, Is.EqualTo(0));
        Assert.That(outcome.StandardOutput, Does.StartWith("git version"));
        Assert.That(outcome.TimedOut, Is.False);
        Assert.That(outcome.LaunchError, Is.Null);
    }

    [Test]
    public async Task Run_returns_a_non_zero_exit_code_without_throwing()
    {
        var outcome = await RunGitAsync(Environment.Root, TimeSpan.FromSeconds(30), ["definitely-not-a-git-command"]);

        Assert.That(outcome.ExitCode, Is.Not.EqualTo(0));
        Assert.That(outcome.Succeeded, Is.False);
    }

    [Test]
    public async Task Run_writes_stdin_and_reads_it_back()
    {
        var repository = NewRepository();

        var outcome = await RunGitAsync(
            repository,
            TimeSpan.FromSeconds(30),
            ["hash-object", "--stdin"],
            standardInput: "hello\n");

        Assert.That(outcome.ExitCode, Is.EqualTo(0));
        Assert.That(outcome.StandardOutput.Trim(), Is.EqualTo("ce013625030ba8dba906f756967f9e9ca394464a"),
            "the object id of 'hello\\n' is fixed by git's hashing, so a mismatch means stdin was not delivered intact");
    }

    [Test]
    public async Task Run_handles_more_than_one_megabyte_of_output_without_deadlock()
    {
        var repository = NewRepository();
        var big = new string('x', 3 * 1024 * 1024) + "\n";
        var file = WriteFile(repository, "big.txt", big);
        var blob = Git.Must(repository, "hash-object", "-w", file);

        var outcome = await RunGitAsync(
            repository,
            TimeSpan.FromSeconds(60),
            ["cat-file", "--batch"],
            standardInput: blob + "\n");

        Assert.That(outcome.ExitCode, Is.EqualTo(0));
        Assert.That(outcome.StandardOutput.Length, Is.GreaterThan(3 * 1024 * 1024),
            "the blob content must come back in full; a truncated read means the pipe was not drained");
        Assert.That(outcome.TimedOut, Is.False);
    }

    [Test]
    public async Task Run_applies_environment_overrides_to_the_child_process()
    {
        var overrides = new Dictionary<string, string?>(Environment.Variables, StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = "Override Author",
            ["GIT_AUTHOR_EMAIL"] = "override@example.invalid"
        };

        var outcome = await RunGitAsync(Environment.Root, TimeSpan.FromSeconds(30), ["var", "GIT_AUTHOR_IDENT"], overrides: overrides);

        Assert.That(outcome.StandardOutput, Does.Contain("Override Author"));
    }

    [Test]
    public async Task Run_reports_a_launch_error_for_a_missing_executable()
    {
        var runner = new GitProcessRunner(Environment.Variables);
        var missing = Path.Combine(Environment.Root, "no-such-executable" + (OperatingSystem.IsWindows() ? ".exe" : ""));

        var outcome = await runner.RunAsync(
            new GitProcessRequest(missing, ["--version"], Environment.Root, TimeSpan.FromSeconds(10), ReadOnly: true, StandardInput: null),
            CancellationToken.None);

        Assert.That(outcome.LaunchError, Is.Not.Null.And.Not.Empty);
        Assert.That(outcome.ExitCode, Is.EqualTo(-1));
    }

    [Test]
    public async Task Run_terminates_the_process_tree_when_the_timeout_elapses()
    {
        var repository = NewRepositoryWithSlowHook();

        var stopwatch = Stopwatch.StartNew();
        var outcome = await RunGitAsync(repository, TimeSpan.FromSeconds(2), ["commit", "--allow-empty", "-m", "slow"]);
        stopwatch.Stop();

        Assert.That(outcome.TimedOut, Is.True);
        Assert.That(outcome.Succeeded, Is.False);
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)),
            "the hook sleeps for 60 seconds; returning sooner proves the process tree was killed");
    }

    [Test]
    public void Run_throws_operation_canceled_and_kills_the_process_when_cancelled()
    {
        var repository = NewRepositoryWithSlowHook();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        Assert.ThrowsAsync<OperationCanceledException>(() => RunGitAsync(
            repository,
            TimeSpan.FromMinutes(5),
            ["commit", "--allow-empty", "-m", "cancelled"],
            cancellationToken: cancellation.Token));
        stopwatch.Stop();

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));
    }

    /// <summary>Creates a repository whose pre-commit hook sleeps, so a commit runs for a long time.</summary>
    private string NewRepositoryWithSlowHook()
    {
        var repository = NewRepositoryWithCommit();
        var hook = Path.Combine(repository, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 60\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return repository;
    }

    private async Task<GitProcessOutcome> RunGitAsync(
        string workingDirectory,
        TimeSpan timeout,
        string[] arguments,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var runner = new GitProcessRunner(overrides ?? Environment.Variables);
        var executable = ResolveGit();
        return await runner.RunAsync(
            new GitProcessRequest(executable, arguments, workingDirectory, timeout, ReadOnly: true, StandardInput: standardInput),
            cancellationToken);
    }

    private static string ResolveGit()
    {
        var resolved = new GitExecutableLocator().Resolve(explicitPath: null, environmentVariables: null);
        Assert.That(resolved.Path, Is.Not.Null, resolved.Message);
        return resolved.Path!;
    }
}
