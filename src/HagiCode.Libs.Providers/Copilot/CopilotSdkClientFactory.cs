using GitHub.Copilot.SDK;

namespace HagiCode.Libs.Providers.Copilot;

internal interface ICopilotSdkClient : IAsyncDisposable
{
    Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);

    Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken);
}

internal interface ICopilotSdkSession : IAsyncDisposable
{
    string SessionId { get; }

    IDisposable On(SessionEventHandler handler);

    Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken);
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
            return new CopilotSdkSessionAdapter(await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false));
        }

        public async Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
        {
            return new CopilotSdkSessionAdapter(await client.ResumeSessionAsync(sessionId, config, cancellationToken).ConfigureAwait(false));
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }

    private sealed class CopilotSdkSessionAdapter(CopilotSession session) : ICopilotSdkSession
    {
        public string SessionId => session.SessionId;

        public IDisposable On(SessionEventHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return session.On(handler);
        }

        public Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            return session.SendAndWaitAsync(options, timeout, cancellationToken);
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
