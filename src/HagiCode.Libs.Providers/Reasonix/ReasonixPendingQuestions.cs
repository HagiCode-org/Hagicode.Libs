using HagiCode.Libs.Core.Acp;
using Microsoft.Extensions.Logging;

namespace HagiCode.Libs.Providers.Reasonix;

/// <summary>
/// The ACP permission hook of one Reasonix execution that has a <see cref="ReasonixOptions.UserInputHandler" />. It routes
/// Reasonix question requests to the handler and leaves every other permission request to the default answer.
/// </summary>
/// <remarks>
/// A question is answered with the option whose label the handler returned. No answer, an answer that is not exactly one
/// offered label, the question timeout and a failing handler decline the question: <c>qN:cancel</c> is selected when the
/// request offers it, otherwise the <c>cancelled</c> outcome is sent, and the execution remembers that it declined a question
/// so the terminal message can be normalized. When the caller cancels the execution, the request is answered as
/// <c>cancelled</c> (when the client can still write) and nothing is recorded as declined.
/// </remarks>
internal sealed class ReasonixPendingQuestions
{
    private readonly Func<ReasonixUserInputRequest, CancellationToken, Task<ReasonixUserInputResponse?>> _handler;
    private readonly TimeSpan _questionTimeout;
    private readonly CancellationToken _callerToken;
    private readonly ILogger _logger;
    private int _declined;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReasonixPendingQuestions" /> class.
    /// </summary>
    /// <param name="handler">The handler that answers the questions.</param>
    /// <param name="questionTimeout">How long a question may stay unanswered, or <see cref="Timeout.InfiniteTimeSpan" />.</param>
    /// <param name="callerToken">The cancellation token of the whole execution.</param>
    /// <param name="logger">The provider logger.</param>
    public ReasonixPendingQuestions(
        Func<ReasonixUserInputRequest, CancellationToken, Task<ReasonixUserInputResponse?>> handler,
        TimeSpan questionTimeout,
        CancellationToken callerToken,
        ILogger logger)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _questionTimeout = questionTimeout;
        _callerToken = callerToken;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a value indicating whether this execution declined a question.
    /// </summary>
    public bool HasDeclinedQuestion => Volatile.Read(ref _declined) != 0;

    /// <summary>
    /// Decides one ACP permission request. It matches <see cref="AcpPermissionRequestHandler" />.
    /// </summary>
    /// <param name="permission">The permission request.</param>
    /// <param name="clientToken">Cancelled when the ACP client is closing.</param>
    /// <returns>The decision for a question request; <see langword="null" /> for any other request.</returns>
    public async Task<AcpPermissionDecision?> HandleAsync(AcpPermissionRequest permission, CancellationToken clientToken)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (!ReasonixUserInputRequest.IsQuestionRequest(permission))
        {
            return null;
        }

        if (!ReasonixUserInputRequest.TryParse(permission, out var request))
        {
            _logger.LogWarning(
                "Reasonix question {QuestionId} has no option the user could choose; it is declined.",
                permission.ToolCallId);
            MarkDeclined();
            return ReasonixUserInputRequest.CreateDecline(permission);
        }

        using var handlerSource = CancellationTokenSource.CreateLinkedTokenSource(_callerToken, clientToken);
        if (_questionTimeout != Timeout.InfiniteTimeSpan)
        {
            handlerSource.CancelAfter(_questionTimeout);
        }

        try
        {
            var handlerTask = _handler(request, handlerSource.Token);
            _ = handlerTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // WaitAsync keeps the timeout and the cancellation effective for a handler that ignores its token.
            var response = await handlerTask.WaitAsync(handlerSource.Token).ConfigureAwait(false);
            var decision = response is null ? request.CreateDecline() : response.ToOutcome(request);
            if (IsDecline(request, decision))
            {
                _logger.LogInformation(
                    "Reasonix question {QuestionId} was declined: the answer is not exactly one offered option.",
                    request.RequestId);
                MarkDeclined();
            }

            return decision;
        }
        catch (OperationCanceledException) when (_callerToken.IsCancellationRequested || clientToken.IsCancellationRequested)
        {
            // The run is being stopped: answer as cancelled if the client can still write, and do not report a decline.
            return AcpPermissionDecision.Cancelled;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Reasonix question {QuestionId} was not answered within {QuestionTimeout}; it is declined.",
                request.RequestId,
                _questionTimeout);
            MarkDeclined();
            return request.CreateDecline();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The user-input handler failed for Reasonix question {QuestionId}; it is declined.", request.RequestId);
            MarkDeclined();
            return request.CreateDecline();
        }
    }

    private static bool IsDecline(ReasonixUserInputRequest request, AcpPermissionDecision decision)
    {
        return decision.IsCancelled ||
               string.Equals(decision.SelectedOptionId, request.CancelOptionId, StringComparison.Ordinal);
    }

    private void MarkDeclined()
    {
        Volatile.Write(ref _declined, 1);
    }
}
