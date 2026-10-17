using System.Text.Json.Serialization;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// An image sent with one Codex CLI turn. The bytes live only for the run that carries them.
/// </summary>
/// <remarks>
/// <para>
/// Privacy: the type never prints or serializes its bytes. <see cref="ToString"/> and JSON show the media type and byte
/// length only, so a diagnostic of the options or the turn cannot leak image data.
/// </para>
/// <para>
/// Lifetime: the app-server channel base64-encodes the bytes into a data URL when the turn input is built and keeps nothing
/// afterwards. The exec channel copies the bytes to a temporary file outside the working directory and deletes that file
/// when the run ends, including on failure and cancellation. Attachments are never part of the reusable session options,
/// so they do not change pooled-entry identity.
/// </para>
/// </remarks>
/// <param name="MediaType">The media type. It must be one of <see cref="SupportedMediaTypes"/>.</param>
/// <param name="Data">The image bytes.</param>
public sealed record CodexImageAttachment(string MediaType, [property: JsonIgnore] ReadOnlyMemory<byte> Data)
{
    /// <summary>The media types Codex CLI accepts for image input.</summary>
    public static readonly IReadOnlyList<string> SupportedMediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>Returns true when <paramref name="mediaType"/> is one of <see cref="SupportedMediaTypes"/>, ignoring case.</summary>
    public static bool IsSupportedMediaType(string? mediaType) =>
        mediaType is not null && SupportedMediaTypes.Any(supported => string.Equals(supported, mediaType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the media type and size. The bytes are never included.</summary>
    public override string ToString() =>
        $"CodexImageAttachment {{ MediaType = {MediaType}, ByteLength = {Data.Length} }}";
}
