namespace HagiCode.Libs.Imaging;

/// <summary>
/// In-process AVIF conversion. Accepts PNG, JPEG, WebP, GIF (first frame) and AVIF sources identified by content.
/// Never starts a child process and never looks up an executable on <c>PATH</c>.
/// </summary>
public interface IAvifConverter
{
    /// <summary>Converts an in-memory image. The AVIF bytes are returned in <see cref="AvifConversionResult.Data"/>.</summary>
    Task<AvifConversionResult> ConvertAsync(
        ReadOnlyMemory<byte> source,
        AvifConversionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts a forward-only or seekable stream into a destination stream. Both streams are left open. Nothing is
    /// written to <paramref name="destination"/> unless the conversion succeeds.
    /// </summary>
    Task<AvifConversionResult> ConvertAsync(
        Stream source,
        Stream destination,
        AvifConversionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts a file. The output is written to a temporary file in the destination directory and moved into place
    /// only after the encode succeeds.
    /// </summary>
    Task<AvifConversionResult> ConvertFileAsync(
        string sourcePath,
        string destinationPath,
        AvifConversionOptions? options = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default);
}
