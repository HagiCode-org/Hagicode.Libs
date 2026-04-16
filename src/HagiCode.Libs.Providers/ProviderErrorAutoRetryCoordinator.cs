using System.Runtime.CompilerServices;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers;

internal static class ProviderErrorAutoRetryCoordinator
{
    public static async IAsyncEnumerable<CliMessage> ExecuteAsync(
        string prompt,
        ProviderErrorAutoRetrySettings? settings,
        Func<string, IAsyncEnumerable<CliMessage>> executeAttemptAsync,
        Func<bool> canRetryInSameContext,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        string retryableTerminalType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(executeAttemptAsync);
        ArgumentNullException.ThrowIfNull(canRetryInSameContext);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(retryableTerminalType);

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
                if (string.Equals(message.Type, retryableTerminalType, StringComparison.OrdinalIgnoreCase))
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
