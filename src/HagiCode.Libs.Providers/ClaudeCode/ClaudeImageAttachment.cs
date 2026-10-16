using System.Text.Json.Serialization;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// An image sent with a Claude Code prompt as a base64 image block. The bytes live only for the run that carries them.
/// The type never prints or serializes its bytes: <see cref="ToString"/> and JSON show the media type and size only, so
/// a diagnostic of the options cannot leak image data.
/// </summary>
/// <param name="MediaType">The media type. It must be one of <see cref="SupportedMediaTypes"/>.</param>
/// <param name="Data">The image bytes. They are base64-encoded once, when the prompt message is built.</param>
public sealed record ClaudeImageAttachment(string MediaType, [property: JsonIgnore] ReadOnlyMemory<byte> Data)
{
    /// <summary>The media types the Claude API accepts for image blocks.</summary>
    public static readonly IReadOnlyList<string> SupportedMediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>Returns true when <paramref name="mediaType"/> is one of <see cref="SupportedMediaTypes"/>, ignoring case.</summary>
    public static bool IsSupportedMediaType(string? mediaType) =>
        mediaType is not null && SupportedMediaTypes.Any(supported => string.Equals(supported, mediaType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the media type and size. The bytes are never included.</summary>
    public override string ToString() =>
        $"ClaudeImageAttachment {{ MediaType = {MediaType}, ByteLength = {Data.Length} }}";
}
