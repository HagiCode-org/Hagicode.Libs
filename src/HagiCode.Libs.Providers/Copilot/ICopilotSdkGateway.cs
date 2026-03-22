namespace HagiCode.Libs.Providers.Copilot;

internal interface ICopilotSdkGateway
{
    Task<ICopilotSdkRuntime> CreateRuntimeAsync(
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default);
}
