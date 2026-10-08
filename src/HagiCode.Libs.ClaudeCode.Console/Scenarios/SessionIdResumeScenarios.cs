using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console.Scenarios;

/// <summary>
/// Scenarios that restore a Claude conversation by explicit id (<c>--resume &lt;id&gt;</c>), the only supported
/// restore path; the provider never emits <c>--continue</c>.
/// </summary>
public static class SessionIdResumeScenarios
{
    public const string SessionIdScenarioName = "Session Restore (Session Id)";

    public const string ResumeIdScenarioName = "Session Restore (Resume Id)";

    /// <summary>
    /// Starts the conversation with a caller-chosen <see cref="ClaudeCodeOptions.SessionId" /> (<c>--session-id</c>)
    /// and restores it with <see cref="ClaudeCodeOptions.Resume" /> set to that same id.
    /// </summary>
    public static ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>> CreateWithExplicitSessionId(
        ClaudeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>(
            SessionIdScenarioName,
            "Verify that a conversation started with --session-id <id> can be restored with --resume <id>.",
            (provider, cancellationToken) => ExecuteAsync(
                provider,
                executionOptions,
                SessionIdScenarioName,
                useExplicitSessionId: true,
                cancellationToken));
    }

    /// <summary>
    /// Starts the conversation without an id, reads the <c>session_id</c> the CLI reported,
    /// and restores it with <see cref="ClaudeCodeOptions.Resume" /> set to that id.
    /// </summary>
    public static ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>> CreateWithCapturedResumeId(
        ClaudeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>(
            ResumeIdScenarioName,
            "Verify that the session_id reported by Claude can be passed to --resume <id> to restore the conversation.",
            (provider, cancellationToken) => ExecuteAsync(
                provider,
                executionOptions,
                ResumeIdScenarioName,
                useExplicitSessionId: false,
                cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeConsoleExecutionOptions executionOptions,
        string scenarioName,
        bool useExplicitSessionId,
        CancellationToken cancellationToken)
    {
        var secret = $"BLUEPRINT-{Guid.NewGuid():N}";
        var requestedSessionId = useExplicitSessionId ? Guid.NewGuid().ToString() : null;

        var firstOptions = executionOptions.CreateBaseOptions() with
        {
            SessionId = requestedSessionId,
            MaxTurns = 1,
        };

        var firstPrompt = $"Remember the secret word: {secret}. Reply with exactly ACK.";
        var (firstMessages, _, reportedSessionId) = await ClaudeScenarioMessageReader.ReadAssistantMessagesWithSessionIdAsync(
            provider,
            firstOptions,
            firstPrompt,
            cancellationToken);

        if (firstMessages.Count == 0)
        {
            return Fail(provider, scenarioName, "Initial request returned no assistant messages.");
        }

        var firstCombined = string.Join(" ", firstMessages);
        if (!firstCombined.Contains("ACK", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(provider, scenarioName, $"Initial request did not acknowledge the setup prompt. Response: {firstCombined}");
        }

        if (string.IsNullOrWhiteSpace(reportedSessionId))
        {
            return Fail(provider, scenarioName, "Initial request did not report a session_id, so the conversation could not be resumed by id.");
        }

        if (requestedSessionId is not null &&
            !string.Equals(requestedSessionId, reportedSessionId, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                provider,
                scenarioName,
                $"Claude did not honor --session-id. Requested: {requestedSessionId}. Reported: {reportedSessionId}");
        }

        var secondOptions = executionOptions.CreateBaseOptions() with
        {
            Resume = reportedSessionId,
            MaxTurns = 1,
        };

        var secondPrompt = "What was the secret word I told you earlier? Reply with just the word.";
        var (secondMessages, _) = await ClaudeScenarioMessageReader.ReadAssistantMessagesAsync(
            provider,
            secondOptions,
            secondPrompt,
            cancellationToken);

        if (secondMessages.Count == 0)
        {
            return Fail(provider, scenarioName, $"Resume request for session {reportedSessionId} returned no assistant messages.");
        }

        var secondCombined = string.Join(" ", secondMessages);
        var normalized = secondCombined.Replace("`", string.Empty, StringComparison.Ordinal).Trim();

        return normalized.Contains(secret, StringComparison.OrdinalIgnoreCase)
            ? new ProviderConsoleScenarioResult(provider.Name, scenarioName, true, 0)
            : Fail(
                provider,
                scenarioName,
                $"Resume request for session {reportedSessionId} did not return the remembered secret. Expected: {secret}. Response: {secondCombined}");
    }

    private static ProviderConsoleScenarioResult Fail(
        ICliProvider<ClaudeCodeOptions> provider,
        string scenarioName,
        string errorMessage)
    {
        return new ProviderConsoleScenarioResult(provider.Name, scenarioName, false, 0, ErrorMessage: errorMessage);
    }
}
