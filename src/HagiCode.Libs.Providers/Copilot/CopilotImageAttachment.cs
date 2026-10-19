using System.Text.Json.Serialization;

namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// An image sent with a Copilot prompt as an inline blob attachment. The bytes live only for the run that carries them.
/// The type never prints or serializes its bytes: <see cref="ToString"/> and JSON show the media type, file name and size
/// only, so a diagnostic of the options cannot leak image data.
/// </summary>
/// <param name="MediaType">The media type, for example <c>image/png</c>. It must start with <c>image/</c>.</param>
/// <param name="Data">The image bytes. They are base64-encoded once, when the attachment is built for the SDK send call.</param>
/// <param name="FileName">The optional file name shown to the agent as the attachment's display name.</param>
public sealed record CopilotImageAttachment(
    string MediaType,
    [property: JsonIgnore] ReadOnlyMemory<byte> Data,
    string? FileName = null)
{
    /// <summary>Returns the media type, file name and size. The bytes are never included.</summary>
    public override string ToString() =>
        $"CopilotImageAttachment {{ MediaType = {MediaType}, FileName = {FileName ?? "null"}, ByteLength = {Data.Length} }}";
}

/// <summary>
/// The image capability that GitHub Copilot reports for one model. A limit that Copilot does not report is <see langword="null" />;
/// it is never replaced by an invented value.
/// </summary>
/// <param name="SupportsVision">True when the model accepts image input.</param>
/// <param name="MaxPromptImages">The maximum number of images in one prompt, when reported.</param>
/// <param name="MaxPromptImageSize">The maximum size of one image in bytes, when reported.</param>
/// <param name="SupportedMediaTypes">The media types the model accepts, when reported.</param>
public sealed record CopilotModelImageCapability(
    bool SupportsVision,
    int? MaxPromptImages = null,
    int? MaxPromptImageSize = null,
    IReadOnlyList<string>? SupportedMediaTypes = null);
