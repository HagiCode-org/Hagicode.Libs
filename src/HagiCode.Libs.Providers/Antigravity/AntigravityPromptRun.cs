using System.Text.Json;
using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Antigravity;

/// <summary>
/// Tracks every prompt of one Antigravity run: the first prompt, the steers sent while it runs, and the answers to questions.
/// </summary>
/// <remarks>
/// Antigravity ends a prompt as soon as a steer or an answer arrives, and each prompt that ends with <c>end_turn</c> also yields a
/// <c>prompt_completed</c> notification. So a completion notification marks a turn boundary, not the end of the run. The run ends
/// only when no prompt is still open, and the boundary check below decides that.
/// </remarks>
internal sealed class AntigravityPromptRun
{
    /// <summary>How long a boundary waits for a prompt that has just answered to be marked as completed.</summary>
    private static readonly TimeSpan BoundarySettleWindow = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly List<PromptAttempt> _attempts = [];
    private readonly IAcpSessionClient _sessionClient;
    private readonly string _sessionId;
    private readonly CancellationToken _cancellationToken;
    private readonly AntigravityRunSteering? _steering;
    private bool _closed;

    public AntigravityPromptRun(
        IAcpSessionClient sessionClient,
        string sessionId,
        CancellationToken cancellationToken,
        AntigravityRunSteering? steering)
    {
        _sessionClient = sessionClient;
        _sessionId = sessionId;
        _cancellationToken = cancellationToken;
        _steering = steering;
        _steering?.Attach(SteerAsync);
    }

    /// <summary>Gets a value indicating whether every prompt started so far has completed.</summary>
    public bool AllCompleted
    {
        get
        {
            lock (_gate)
            {
                return _attempts.All(static attempt => attempt.Result.IsCompleted);
            }
        }
    }

    /// <summary>Gets the number of prompts started so far.</summary>
    public int AttemptCount
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count;
            }
        }
    }

    /// <summary>Gets the result of the most recently started prompt.</summary>
    public Task<JsonElement> LatestResult
    {
        get
        {
            lock (_gate)
            {
                return _attempts[^1].Result;
            }
        }
    }

    /// <summary>Gets the failure of the most recently started prompt, or <see langword="null"/> when it has not failed.</summary>
    public Exception? LatestFailure
    {
        get
        {
            lock (_gate)
            {
                var latest = _attempts[^1].Result;
                return latest.IsFaulted ? latest.Exception?.InnerException ?? latest.Exception : null;
            }
        }
    }

    /// <summary>Starts a prompt on the session, as the first turn, an answer, or a steer.</summary>
    public Task<JsonElement> Start(string text, string? steerId = null, IReadOnlyList<AcpImageBlock>? images = null)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("The Antigravity run has ended, so no prompt can be started on it.");
            }

            // With no image the text-only overload sends the same frame as before image support existed.
            var result = images is { Count: > 0 }
                ? _sessionClient.SendPromptAsync(_sessionId, text, images, _cancellationToken)
                : _sessionClient.SendPromptAsync(_sessionId, text, _cancellationToken);
            _attempts.Add(new PromptAttempt(result, ObserveAsync(result, steerId)));
            return result;
        }
    }

    /// <summary>Snapshot of the result tasks of every prompt started so far.</summary>
    public Task<JsonElement>[] SnapshotResults()
    {
        lock (_gate)
        {
            return _attempts.Select(static attempt => attempt.Result).ToArray();
        }
    }

    /// <summary>Waits until every prompt started so far has also reported its steer outcome, if any.</summary>
    public Task SettledAsync()
    {
        Task[] settled;
        lock (_gate)
        {
            settled = _attempts.Select(static attempt => attempt.Settled).ToArray();
        }

        return Task.WhenAll(settled);
    }

    /// <summary>
    /// Decides whether a turn boundary ends the run. A prompt that has just answered may not be marked completed yet, so an open
    /// prompt is given a short window to finish before the decision.
    /// </summary>
    /// <returns><see langword="true" /> when a prompt is still open, so the boundary is not the end of the run.</returns>
    public async Task<bool> HasOpenPromptsAsync()
    {
        Task[] open;
        lock (_gate)
        {
            open = _attempts.Where(static attempt => !attempt.Result.IsCompleted).Select(static attempt => (Task)attempt.Result).ToArray();
        }

        if (open.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(open), Task.Delay(BoundarySettleWindow)).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return _attempts.Any(static attempt => !attempt.Result.IsCompleted);
        }
    }

    /// <summary>
    /// Ends the run when no prompt is open. The check and the close happen under one lock, so a steer cannot start in between.
    /// </summary>
    /// <returns><see langword="false" /> when a prompt is open, so the run must go on.</returns>
    public bool TryCloseWhenIdle()
    {
        lock (_gate)
        {
            if (_attempts.Any(static attempt => !attempt.Result.IsCompleted))
            {
                return false;
            }

            _closed = true;
        }

        _steering?.Detach();
        return true;
    }

    /// <summary>Stops accepting steers and answers. Prompts already started are left to finish or be cancelled.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }

        _steering?.Detach();
    }

    private Task<AntigravitySteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // A steer is only meaningful once the first prompt is running, and only while the run is still open.
            if (_closed || _attempts.Count == 0)
            {
                return Task.FromResult(AntigravitySteerResult.NotSteerable);
            }

            Start(text, steerId);
        }

        return Task.FromResult(AntigravitySteerResult.Sent);
    }

    private async Task ObserveAsync(Task<JsonElement> result, string? steerId)
    {
        // Observed here so that a failed prompt never goes unobserved. The failure itself is read through LatestResult.
        try
        {
            var promptResult = await result.ConfigureAwait(false);
            if (steerId is not null)
            {
                _steering?.Report(
                    steerId,
                    AntigravityAcpMessageMapper.IsFailurePromptResult(promptResult)
                        ? AntigravitySteerOutcome.NotConsumed
                        : AntigravitySteerOutcome.Consumed);
            }
        }
        catch (Exception) when (steerId is not null)
        {
            _steering?.Report(steerId, AntigravitySteerOutcome.NotConsumed);
        }
        catch (Exception)
        {
        }
    }

    private sealed record PromptAttempt(Task<JsonElement> Result, Task Settled);
}
