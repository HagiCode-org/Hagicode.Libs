namespace HagiCode.Libs.Providers.Copilot;

internal interface ICopilotSdkRuntime : IAsyncDisposable
{
    string SessionId { get; }

    IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default);
}
