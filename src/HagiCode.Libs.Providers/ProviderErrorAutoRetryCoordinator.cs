using System.Runtime.CompilerServices;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers;

internal static class ProviderErrorAutoRetryCoordinator
{
    public static IAsyncEnumerable<CliMessage> ExecuteAsync(
        string prompt,
        ProviderErrorAutoRetrySettings? settings,
        Func<string, IAsyncEnumerable<CliMessage>> executeAttemptAsync,
        Func<bool> canRetryInSameContext,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        string retryableTerminalType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retryableTerminalType);

        return ExecuteCoreAsync(
            prompt,
            settings,
            executeAttemptAsync,
            canRetryInSameContext,
            delayAsync,
            message => string.Equals(message.Type, retryableTerminalType, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    public static IAsyncEnumerable<CliMessage> ExecuteAsync(
        string prompt,
        ProviderErrorAutoRetrySettings? settings,
        Func<string, IAsyncEnumerable<CliMessage>> executeAttemptAsync,
        Func<bool> canRetryInSameContext,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<CliMessage, bool> isRetryableTerminalMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isRetryableTerminalMessage);

        return ExecuteCoreAsync(
            prompt,
            settings,
            executeAttemptAsync,
            canRetryInSameContext,
            delayAsync,
            isRetryableTerminalMessage,
            cancellationToken);
    }

    private static async IAsyncEnumerable<CliMessage> ExecuteCoreAsync(
        string prompt,
        ProviderErrorAutoRetrySettings? settings,
        Func<string, IAsyncEnumerable<CliMessage>> executeAttemptAsync,
        Func<bool> canRetryInSameContext,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<CliMessage, bool> isRetryableTerminalMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(executeAttemptAsync);
        ArgumentNullException.ThrowIfNull(canRetryInSameContext);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(isRetryableTerminalMessage);

        var normalizedSettings = ProviderErrorAutoRetrySettings.Normalize(settings);
        var retrySchedule = normalizedSettings.Enabled
            ? normalizedSettings.GetRetrySchedule()
            : [];

        for (var attempt = 0; ; attempt++)
        {
            var attemptPrompt = attempt == 0
                ? prompt
                : ProviderErrorAutoRetrySettings.ContinuationPrompt;
            CliMessage? terminalFailure = null;

            await foreach (var message in executeAttemptAsync(attemptPrompt).WithCancellation(cancellationToken))
            {
                // Providers may admit a narrow terminal `error` envelope here, but the coordinator
                // still owns the retry snapshot and same-session/thread gating.
                if (isRetryableTerminalMessage(message))
                {
                    // Hide intermediate terminal failures so callers only see the final failure after retries exhaust.
                    terminalFailure = message;
                    break;
                }

                yield return message;
            }

            if (terminalFailure is null)
            {
                yield break;
            }

            if (attempt >= retrySchedule.Count || !canRetryInSameContext())
            {
                yield return terminalFailure;
                yield break;
            }

            await delayAsync(retrySchedule[attempt], cancellationToken).ConfigureAwait(false);
        }
    }
}
