namespace HagiCode.Libs.Imaging.Internal;

/// <summary>
/// Identifies the source format from its leading bytes. This is the allow-list: anything not matched here is rejected
/// before the native library is touched. File names and extensions are never consulted.
/// </summary>
internal static class SourceSniffer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns the detected format, or <c>null</c> when the bytes are not in the allow-list.</summary>
    internal static AvifSourceFormat? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSignature))
        {
            return AvifSourceFormat.Png;
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return AvifSourceFormat.Jpeg;
        }

        if (head.Length >= 6 && (head.Slice(0, 6).SequenceEqual("GIF87a"u8) || head.Slice(0, 6).SequenceEqual("GIF89a"u8)))
        {
            return AvifSourceFormat.Gif;
        }

        if (head.Length >= 12 && head.Slice(0, 4).SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return AvifSourceFormat.WebP;
        }

        if (AvifFormat.IsAvif(head))
        {
            return AvifSourceFormat.Avif;
        }

        return null;
    }
}
