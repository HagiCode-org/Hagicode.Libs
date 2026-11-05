namespace HagiCode.Libs.Providers.Dsh;

/// <summary>
/// One prepared chat image for a dsh prompt. The bytes live only in memory for the duration of the prompt:
/// they are never written to disk, stored in the conversation, or logged.
/// </summary>
/// <param name="ImageId">The image identifier, echoed in the delivery result.</param>
/// <param name="MediaType">The media type, for example <c>image/png</c>.</param>
/// <param name="Bytes">The prepared image bytes.</param>
/// <param name="ManifestLine">The path manifest row used when the image is not inlined.</param>
public sealed record DshImageAttachment(string ImageId, string MediaType, byte[] Bytes, string ManifestLine);
