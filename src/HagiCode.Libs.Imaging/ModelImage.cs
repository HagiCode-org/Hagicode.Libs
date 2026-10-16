namespace HagiCode.Libs.Imaging;

/// <summary>What the preparer did with one source image.</summary>
public enum ModelImageOutcome
{
    /// <summary>The source was already a supported format within the limits and is returned byte for byte.</summary>
    PassedThrough = 1,

    /// <summary>An SVG document was rasterized to PNG.</summary>
    Rasterized = 2,

    /// <summary>The source was downscaled and re-encoded to fit the limits.</summary>
    Reduced = 3,

    /// <summary>The image cannot be sent to a model. <see cref="PreparedImage.Reason"/> says why.</summary>
    NotPreparable = 4,
}

/// <summary>
/// The limits a prepared image must meet. The defaults are the Claude image limits: 5 MiB per image and 8000 pixels per
/// side. <see cref="MaxDecodePixels"/> is checked from the header before any pixel is decoded.
/// </summary>
public sealed record ModelImageLimits
{
    /// <summary>The default limits.</summary>
    public static ModelImageLimits Default { get; } = new();

    /// <summary>The largest encoded size of a prepared image, in bytes. Defaults to 5 MiB.</summary>
    public long MaxBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>The largest width and height of a prepared image, in pixels. Defaults to 8000.</summary>
    public int MaxDimension { get; init; } = 8000;

    /// <summary>The largest width times height that may be decoded. Defaults to 64,000,000 pixels.</summary>
    public long MaxDecodePixels { get; init; } = 64_000_000;

    /// <summary>The largest source file that is read into memory. Defaults to 64 MiB.</summary>
    public long MaxSourceBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value is not positive.</summary>
    public void Validate()
    {
        if (MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "The byte limit must be positive.");
        }

        if (MaxDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDimension), MaxDimension, "The dimension limit must be positive.");
        }

        if (MaxDecodePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDecodePixels), MaxDecodePixels, "The decode pixel limit must be positive.");
        }

        if (MaxSourceBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSourceBytes), MaxSourceBytes, "The source byte limit must be positive.");
        }
    }
}

/// <summary>
/// The result of preparing one image. <see cref="Bytes"/> is empty when the outcome is
/// <see cref="ModelImageOutcome.NotPreparable"/>. <see cref="Reason"/> is a short fixed phrase and never contains image data.
/// </summary>
/// <param name="Outcome">What happened to the image.</param>
/// <param name="MediaType">The media type of <paramref name="Bytes"/>, or <c>null</c> when not preparable.</param>
/// <param name="Bytes">The image to send. Empty when not preparable.</param>
/// <param name="OriginalByteLength">The size of the source file.</param>
/// <param name="PreparedByteLength">The size of <paramref name="Bytes"/>.</param>
/// <param name="Reason">Why the image is not preparable, or a short note on how it was changed. Null for a pass-through.</param>
public sealed record PreparedImage(
    ModelImageOutcome Outcome,
    string? MediaType,
    ReadOnlyMemory<byte> Bytes,
    long OriginalByteLength,
    long PreparedByteLength,
    string? Reason)
{
    /// <summary>True when the image can be sent to a model.</summary>
    public bool IsUsable => Outcome != ModelImageOutcome.NotPreparable;

    /// <summary>Keeps image bytes out of logs and diagnostics. Only sizes and the outcome are printed.</summary>
    public override string ToString() =>
        $"PreparedImage {{ Outcome = {Outcome}, MediaType = {MediaType ?? "(none)"}, OriginalByteLength = {OriginalByteLength}, PreparedByteLength = {PreparedByteLength} }}";
}

/// <summary>
/// Turns a stored image into one a model accepts, in memory. The stored file is never changed. The preparer never
/// throws for bad input: every failure is a <see cref="PreparedImage"/> with <see cref="ModelImageOutcome.NotPreparable"/>.
/// Cancellation is the only exception it raises.
/// </summary>
public interface IModelImagePreparer
{
    /// <summary>Prepares the image stored at <paramref name="path"/>. The file name and extension are not used for detection.</summary>
    Task<PreparedImage> PrepareAsync(string path, ModelImageLimits? limits = null, CancellationToken cancellationToken = default);

    /// <summary>Prepares an image held in memory.</summary>
    Task<PreparedImage> PrepareAsync(ReadOnlyMemory<byte> source, ModelImageLimits? limits = null, CancellationToken cancellationToken = default);
}
