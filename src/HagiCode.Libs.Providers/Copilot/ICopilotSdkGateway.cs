namespace HagiCode.Libs.Providers.Copilot;

internal interface ICopilotSdkGateway
{
    IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default);
}
