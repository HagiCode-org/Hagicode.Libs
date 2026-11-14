using GitHub.Copilot;

namespace HagiCode.Libs.Providers.Copilot;

internal interface ICopilotSdkClient : IAsyncDisposable
{
    Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);

    Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken);

    Task<IReadOnlyList<CopilotSdkModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
}

internal interface ICopilotSdkSession : IAsyncDisposable
{
    string SessionId { get; }

    IDisposable On(Action<SessionEvent> handler);

    Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken);

    // Returns once the message is queued. The session's message id is not needed: steers are matched by their text.
    Task SendAsync(MessageOptions options, CancellationToken cancellationToken);
}

internal interface ICopilotSdkClientFactory
{
    ICopilotSdkClient Create(CopilotClientOptions options);
}

internal sealed class GitHubCopilotSdkClientFactory : ICopilotSdkClientFactory
{
    public ICopilotSdkClient Create(CopilotClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CopilotSdkClientAdapter(new CopilotClient(options));
    }

    private sealed class CopilotSdkClientAdapter(CopilotClient client) : ICopilotSdkClient
    {
        public async Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            var session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
            return await ValidateFusionSessionAsync(session, config, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
        {
            var session = await client.ResumeSessionAsync(sessionId, config, cancellationToken).ConfigureAwait(false);
            return await ValidateFusionSessionAsync(session, config, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<ICopilotSdkSession> ValidateFusionSessionAsync(
            CopilotSession session, SessionConfigBase config, CancellationToken cancellationToken)
        {
            try
            {
                if (CopilotFusionCompatibility.IsFusionModel(config.Model))
                {
                    // Cold resume may not have tier metadata until the session model catalog has been read.
                    var catalog = await session.Rpc.Model.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    var current = await session.Rpc.Model.GetCurrentAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
#pragma warning disable GHCP001 // Fusion is exposed as an experimental typed SDK API.
                    var requestedTier = config.Capi?.FusionTier is { } tier
                        ? CopilotFusionTierJsonConverter.TryParseWireValue(tier.Value, out var parsed)
                            ? parsed
                            : throw new InvalidOperationException($"Unexpected SDK Fusion tier '{tier.Value}'.")
                        : (CopilotFusionTier?)null;
                    var actualTier = current.FusionTier;
                    if (actualTier is null && catalog.Auto is { } routing)
                    {
                        // An omitted preference uses the account's declared default, not a hard-coded Fusion tier.
                        var preference = current.AutoTier?.Value ?? routing.DefaultTier;
                        actualTier = routing.Tiers.FirstOrDefault(candidate => candidate.Id == preference)?.FusionTier;
                    }
#pragma warning restore GHCP001
                    CopilotFusionCompatibility.EnsureRuntimeApplied(config.Model, requestedTier, current.ModelId, actualTier);
                }

                return new CopilotSdkSessionAdapter(session);
            }
            catch (Exception exception)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                if (CopilotFusionCompatibility.IsFusionModel(config.Model)
                    && exception is not OperationCanceledException and not CopilotFusionCompatibilityException)
                {
                    throw new CopilotFusionCompatibilityException(
                        $"[copilot_fusion_unsupported] Could not verify the runtime's Fusion configuration. " +
                        $"No prompt was sent. {exception.Message}", exception);
                }

                throw;
            }
        }

        public async Task<IReadOnlyList<CopilotSdkModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            return models
                .Select(static model => new CopilotSdkModelInfo(
                    model.Id,
                    model.Capabilities?.Supports?.Vision,
                    model.Capabilities?.Limits?.Vision?.MaxPromptImages,
                    model.Capabilities?.Limits?.Vision?.MaxPromptImageSize,
                    model.Capabilities?.Limits?.Vision?.SupportedMediaTypes?.ToList()))
                .ToList();
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }

    private sealed class CopilotSdkSessionAdapter(CopilotSession session) : ICopilotSdkSession
    {
        public string SessionId => session.SessionId;

        public IDisposable On(Action<SessionEvent> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return session.On<SessionEvent>(handler);
        }

        public Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            return session.SendAndWaitAsync(options, timeout, cancellationToken);
        }

        // The SDK call is made synchronously, before the first await, so the caller's gate orders the send.
        public async Task SendAsync(MessageOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            await session.SendAsync(options, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
