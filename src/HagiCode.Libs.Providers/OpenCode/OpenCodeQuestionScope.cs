using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Pauses and re-arms the turn deadline (the linked token of <see cref="OpenCodeHttpClient.PromptAsync(string, OpenCodeSessionPromptRequest, OpenCodeTurnInteraction?, CancellationToken)"/>)
/// while questions are pending. The first pending question pauses the deadline; the last one settled re-arms it with the time that
/// was left. Infinite timeouts are never paused.
/// </summary>
internal sealed class TurnDeadline
{
    private readonly CancellationTokenSource _source;
    private readonly bool _finite;
    private readonly object _sync = new();
    private TimeSpan _remaining;
    private long _armedAt;
    private int _pauses;
    private bool _closed;

    public TurnDeadline(CancellationTokenSource source, TimeSpan timeout)
    {
        _source = source;
        _finite = timeout != Timeout.InfiniteTimeSpan;
        if (_finite)
        {
            _remaining = timeout;
            _armedAt = Stopwatch.GetTimestamp();
            _source.CancelAfter(timeout);
        }
    }

    /// <summary>Stops the deadline while a question is pending. Nested pauses count.</summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (!_finite || _closed)
            {
                return;
            }

            if (_pauses++ == 0)
            {
                _remaining -= Stopwatch.GetElapsedTime(_armedAt);
                _source.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Re-arms the deadline with the time that was left when it was paused, once every pause is released.</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (!_finite || _closed || _pauses == 0 || --_pauses > 0)
            {
                return;
            }

            if (_remaining <= TimeSpan.Zero)
            {
                _source.Cancel();
                return;
            }

            _armedAt = Stopwatch.GetTimestamp();
            _source.CancelAfter(_remaining);
        }
    }

    /// <summary>Makes later pauses and resumes no-ops. Called before the source is disposed.</summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }
    }
}

/// <summary>
/// Runs the questions of one OpenCode turn. A question form of the turn's session starts its handler on a background task and
/// is answered, declined or timed out from there; a question with no handler is declined at once. The scope closes every
/// question still pending when the turn ends, and interrupts the session when an interactive turn was cancelled by the caller.
/// </summary>
internal sealed class OpenCodeQuestionScope : IDisposable
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly OpenCodeHttpClient _client;
    private readonly string _sessionId;
    private readonly OpenCodeTurnInteraction? _interaction;
    private readonly TurnDeadline _deadline;
    private readonly CancellationTokenSource _turnEnded;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    public OpenCodeQuestionScope(
        OpenCodeHttpClient client,
        string sessionId,
        OpenCodeTurnInteraction? interaction,
        TurnDeadline deadline,
        CancellationToken turnToken)
    {
        _client = client;
        _sessionId = sessionId;
        _interaction = interaction;
        _deadline = deadline;
        _turnEnded = CancellationTokenSource.CreateLinkedTokenSource(turnToken);
    }

    /// <summary>
    /// Handles one <c>form.created</c> event of the turn's session. Forms that are not questions are left untouched.
    /// </summary>
    public async Task OnFormCreatedAsync(OpenCodeFormInfo form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!string.Equals(form.Kind, OpenCodeFormKinds.Question, StringComparison.Ordinal) || string.IsNullOrEmpty(form.Id))
        {
            return;
        }

        if (_interaction is null)
        {
            await _client.CancelFormAsync(_sessionId, form.Id, OpenCodeUserInput.NoHandlerMessage, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!OpenCodeUserInput.TryCreateRequest(form, out var request))
        {
            await _client.CancelFormAsync(_sessionId, form.Id, OpenCodeUserInput.DeclinedMessage, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_pending.TryAdd(form.Id, 0))
        {
            return;
        }

        _deadline.Pause();
        _interaction.OnQuestionAsked?.Invoke(request);
        _ = Task.Run(() => RunQuestionAsync(form.Id, request, _interaction));
    }

    /// <summary>
    /// Closes the scope when the turn ends. Every pending question is cancelled with <see cref="OpenCodeUserInput.TurnEndedMessage"/>;
    /// when <paramref name="interruptSession"/> is set, the session is interrupted too. Both calls use a short independent token,
    /// because the turn token is usually already cancelled. Failures are ignored: the turn's own result is what the caller sees.
    /// </summary>
    public async Task CloseAsync(bool interruptSession)
    {
        _deadline.Close();
        _turnEnded.Cancel();

        var interrupt = interruptSession && _interaction is not null;
        if (_pending.IsEmpty && !interrupt)
        {
            return;
        }

        using var independent = new CancellationTokenSource(CloseTimeout);
        foreach (var formId in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(formId, out _))
            {
                await CancelBestEffortAsync(formId, OpenCodeUserInput.TurnEndedMessage, independent.Token).ConfigureAwait(false);
            }
        }

        if (interrupt)
        {
            try
            {
                await _client.InterruptSessionAsync(_sessionId, independent.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsBestEffortFailure(exception))
            {
            }
        }
    }

    public void Dispose()
    {
        _turnEnded.Dispose();
    }

    private async Task RunQuestionAsync(string formId, OpenCodeUserInputRequest request, OpenCodeTurnInteraction interaction)
    {
        try
        {
            using var questionSource = CancellationTokenSource.CreateLinkedTokenSource(_turnEnded.Token);
            if (interaction.QuestionTimeout != Timeout.InfiniteTimeSpan)
            {
                questionSource.CancelAfter(interaction.QuestionTimeout);
            }

            OpenCodeUserInputResponse? response = null;
            var timedOut = false;
            try
            {
                response = await interaction.Handler(request, questionSource.Token)
                    .WaitAsync(questionSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (questionSource.IsCancellationRequested)
            {
                if (_turnEnded.IsCancellationRequested)
                {
                    // The run ended; the close path cancels the form.
                    return;
                }

                timedOut = true;
            }
            catch (Exception)
            {
                // A handler that throws declines the question.
                response = null;
            }

            if (_turnEnded.IsCancellationRequested)
            {
                return;
            }

            // The reply and the cancel use their own short token, not the turn's, so the form is settled even when the turn is ending.
            using var settle = new CancellationTokenSource(CloseTimeout);
            try
            {
                if (timedOut)
                {
                    await _client.CancelFormAsync(_sessionId, formId, OpenCodeUserInput.TimeoutMessage, settle.Token).ConfigureAwait(false);
                }
                else if (response is null)
                {
                    await _client.CancelFormAsync(_sessionId, formId, OpenCodeUserInput.DeclinedMessage, settle.Token).ConfigureAwait(false);
                }
                else
                {
                    var answer = OpenCodeUserInput.CreateFormAnswer(request, response);
                    try
                    {
                        await _client.ReplyFormAsync(_sessionId, formId, answer, settle.Token).ConfigureAwait(false);
                    }
                    catch (OpenCodeApiException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
                    {
                        // OpenCode refused the shape of the answer and the form stays pending: decline it so the turn goes on.
                        await _client.CancelFormAsync(_sessionId, formId, OpenCodeUserInput.DeclinedMessage, settle.Token).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Release(formId);
            }
        }
        catch (Exception)
        {
            // A background task has no caller. The close path cancels whatever is still pending.
        }
    }

    private async Task CancelBestEffortAsync(string formId, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _client.CancelFormAsync(_sessionId, formId, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsBestEffortFailure(exception))
        {
        }
    }

    /// <summary>
    /// Releases a pending question and re-arms the deadline when it was the last one. Only the caller that removes the entry
    /// releases it, so each pause is resumed once.
    /// </summary>
    private void Release(string formId)
    {
        if (_pending.TryRemove(formId, out _))
        {
            _deadline.Resume();
        }
    }

    private static bool IsBestEffortFailure(Exception exception)
    {
        return exception is OpenCodeApiException or HttpRequestException or OperationCanceledException or InvalidOperationException;
    }
}
