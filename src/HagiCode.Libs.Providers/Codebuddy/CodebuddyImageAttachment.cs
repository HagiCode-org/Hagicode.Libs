namespace HagiCode.Libs.Providers.Codebuddy;

/// <summary>
/// One prepared chat image for a CodeBuddy prompt. The bytes live only in memory for the duration of the execution: they are
/// never written to disk, stored in the conversation, or logged.
/// </summary>
/// <param name="ImageId">The image identifier, echoed in the delivery result.</param>
/// <param name="MediaType">The media type, for example <c>image/png</c>.</param>
/// <param name="Bytes">The prepared image bytes.</param>
/// <param name="ManifestLine">The path manifest row, appended to the prompt text when the image is not sent inline.</param>
public sealed record CodebuddyImageAttachment(string ImageId, string MediaType, byte[] Bytes, string ManifestLine)
{
    /// <summary>
    /// Returns the identifier, media type and byte count only. The bytes never appear in the text form.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(CodebuddyImageAttachment)} {{ ImageId = {ImageId}, MediaType = {MediaType}, ByteCount = {Bytes?.Length ?? 0} }}";
    }
}
