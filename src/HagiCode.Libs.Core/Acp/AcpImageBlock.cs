namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents one inline image content block for an ACP <c>session/prompt</c> request.
/// </summary>
public sealed class AcpImageBlock
{
    /// <summary>
    /// Initializes a new inline image block.
    /// </summary>
    /// <param name="mimeType">The media type, for example <c>image/png</c>. The caller validates the allowed set.</param>
    /// <param name="base64Data">The canonical standard base64 image bytes, without a <c>data:</c> prefix.</param>
    public AcpImageBlock(string mimeType, string base64Data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Data);

        MimeType = mimeType;
        Base64Data = base64Data;
    }

    /// <summary>
    /// Gets the image media type.
    /// </summary>
    public string MimeType { get; }

    /// <summary>
    /// Gets the canonical standard base64 image bytes.
    /// </summary>
    public string Base64Data { get; }
}
