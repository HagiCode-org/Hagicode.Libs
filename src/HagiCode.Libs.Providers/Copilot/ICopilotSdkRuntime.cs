namespace HagiCode.Libs.Providers.Copilot;

internal interface ICopilotSdkRuntime : IAsyncDisposable
{
    IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default);
}
