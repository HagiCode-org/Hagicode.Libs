using System.Text.Json.Serialization;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// One prepared chat image for a Pi RPC prompt. The bytes live only in process memory for the duration of the run: they are
/// never written to disk, stored in the conversation, or printed. <see cref="ToString" /> and JSON serialization carry the media
/// type and the byte length only.
/// </summary>
/// <param name="MediaType">The media type, for example <c>image/png</c>.</param>
/// <param name="Bytes">The prepared image bytes.</param>
public sealed record PiImageAttachment(string MediaType, [property: JsonIgnore] byte[] Bytes)
{
    /// <summary>
    /// Gets the number of bytes in the prepared image.
    /// </summary>
    public int ByteLength => Bytes.Length;

    /// <inheritdoc />
    public override string ToString() => $"PiImageAttachment {{ MediaType = {MediaType}, ByteLength = {Bytes.Length} }}";
}
