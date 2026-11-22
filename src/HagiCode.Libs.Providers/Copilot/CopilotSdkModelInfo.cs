namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// One model from the SDK model list, reduced to the fields this library reads. The SDK types stay in the gateway.
/// </summary>
internal sealed record CopilotSdkModelInfo(
    string Id,
    bool? SupportsVision,
    long? MaxPromptImages,
    long? MaxPromptImageSize,
    IReadOnlyList<string>? SupportedMediaTypes);
