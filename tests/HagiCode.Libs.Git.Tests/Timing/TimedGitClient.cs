using System.Diagnostics;
using NUnit.Framework;

namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>
/// Decorator through which every test obtains <see cref="IGitClient" />. Each call is timed with a monotonic clock and written to
/// <see cref="DurationRecorder" />. Tests need no timing code of their own.
/// </summary>
public sealed class TimedGitClient : IGitClient
{
    private readonly IGitClient _inner;
    private readonly DurationRecorder _recorder;

    public TimedGitClient(IGitClient inner, DurationRecorder? recorder = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _recorder = recorder ?? DurationRecorder.Shared;
    }

    public Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default) =>
        Measure(nameof(IsRepositoryAsync), () => _inner.IsRepositoryAsync(path, cancellationToken), _ => (true, null));

    public Task<GitResult<string>> FindRepositoryRootAsync(string path, CancellationToken cancellationToken = default) =>
        Measure(nameof(FindRepositoryRootAsync), () => _inner.FindRepositoryRootAsync(path, cancellationToken), Outcome);

    public Task<GitResult<string?>> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(GetRemoteUrlAsync), () => _inner.GetRemoteUrlAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult<GitIdentity>> GetIdentityAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(GetIdentityAsync), () => _inner.GetIdentityAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult<string>> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(GetCurrentBranchAsync), () => _inner.GetCurrentBranchAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult<bool>> BranchExistsAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default) =>
        Measure(nameof(BranchExistsAsync), () => _inner.BranchExistsAsync(repositoryPath, branchName, cancellationToken), Outcome);

    public Task<GitResult<bool>> IsBranchNameValidAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default) =>
        Measure(nameof(IsBranchNameValidAsync), () => _inner.IsBranchNameValidAsync(repositoryPath, branchName, cancellationToken), Outcome);

    public Task<GitResult<GitDirtyProbe>> ProbeDirtyAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(ProbeDirtyAsync), () => _inner.ProbeDirtyAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult<GitStatus>> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(GetStatusAsync), () => _inner.GetStatusAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult> StageAsync(string repositoryPath, string path, CancellationToken cancellationToken = default) =>
        Measure(nameof(StageAsync), () => _inner.StageAsync(repositoryPath, path, cancellationToken), Outcome);

    public Task<GitResult> UnstageAsync(string repositoryPath, string path, CancellationToken cancellationToken = default) =>
        Measure(nameof(UnstageAsync), () => _inner.UnstageAsync(repositoryPath, path, cancellationToken), Outcome);

    public Task<GitResult<int>> StageAllAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(StageAllAsync), () => _inner.StageAllAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult<int>> UnstageAllAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(UnstageAllAsync), () => _inner.UnstageAllAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult<GitCommitOutcome>> CommitAsync(string repositoryPath, string message, CancellationToken cancellationToken = default) =>
        Measure(nameof(CommitAsync), () => _inner.CommitAsync(repositoryPath, message, cancellationToken), Outcome);

    public Task<GitResult<GitSyncOutcome>> PushAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(PushAsync), () => _inner.PushAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitResult<GitSyncOutcome>> PullAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(PullAsync), () => _inner.PullAsync(repositoryPath, cancellationToken), Outcome);

    public Task<GitRebaseOutcome> RebaseAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(RebaseAsync), () => _inner.RebaseAsync(repositoryPath, cancellationToken), r => (r.Success, r.Failure?.Code.ToString()));

    public Task<GitResult<GitCloneOutcome>> EnsureCloneAsync(string remoteUrl, string targetPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(EnsureCloneAsync), () => _inner.EnsureCloneAsync(remoteUrl, targetPath, cancellationToken), Outcome);

    public Task<GitResult<IReadOnlyList<string>>> ScanForRepositoriesAsync(
        string rootPath,
        GitScanOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Measure(nameof(ScanForRepositoriesAsync), () => _inner.ScanForRepositoriesAsync(rootPath, options, cancellationToken), Outcome);

    public Task<GitResult<string?>> GetConfigValueAsync(string repositoryPath, string key, GitConfigScope scope, CancellationToken cancellationToken = default) =>
        Measure(nameof(GetConfigValueAsync), () => _inner.GetConfigValueAsync(repositoryPath, key, scope, cancellationToken), Outcome);

    public Task<GitResult<GitTrackingInfo?>> GetTrackingInfoAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Measure(nameof(GetTrackingInfoAsync), () => _inner.GetTrackingInfoAsync(repositoryPath, cancellationToken), Outcome);

    private static (bool Success, string? Code) Outcome<T>(GitResult<T> result) =>
        (result.Success, result.Failure?.Code.ToString());

    private static (bool Success, string? Code) Outcome(GitResult result) =>
        (result.Success, result.Failure?.Code.ToString());

    private async Task<T> Measure<T>(string operation, Func<Task<T>> call, Func<T, (bool Success, string? Code)> outcome)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await call().ConfigureAwait(false);
            var (success, code) = outcome(result);
            Write(operation, start, success, code);
            return result;
        }
        catch (Exception exception)
        {
            Write(operation, start, success: false, failureCode: exception.GetType().Name);
            throw;
        }
    }

    private void Write(string operation, long start, bool success, string? failureCode)
    {
        var elapsed = Stopwatch.GetElapsedTime(start);
        var scope = TimingContext.Scope;
        var testName = TestContext.CurrentContext.Test.FullName;

        _recorder.Add(new DurationRecord(
            Operation: operation,
            Scenario: scope?.Scenario ?? "functional",
            TestName: testName,
            Iteration: scope?.Iteration ?? 0,
            WarmUp: scope?.WarmUp ?? false,
            Category: scope?.Category ?? TimingCategories.Operation,
            DurationMs: elapsed.TotalMilliseconds,
            Success: success,
            FailureCode: failureCode));
    }
}
