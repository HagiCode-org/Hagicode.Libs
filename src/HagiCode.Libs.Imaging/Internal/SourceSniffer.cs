namespace HagiCode.Libs.Imaging.Internal;

/// <summary>
/// Identifies the source format from its leading bytes. This is the allow-list: anything not matched here is rejected
/// before the native library is touched. File names and extensions are never consulted.
/// </summary>
internal static class SourceSniffer
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// True when the bytes begin an SVG document: an optional UTF-8 BOM and whitespace, then any number of XML
    /// prolog declarations, comments and DOCTYPE declarations, then an <c>svg</c> root element. The file name and
    /// extension are never consulted. The scan stops at the first token that is none of these.
    /// </summary>
    internal static bool IsSvg(ReadOnlySpan<byte> head)
    {
        var rest = head.StartsWith(Utf8Bom) ? head[Utf8Bom.Length..] : head;
        while (true)
        {
            rest = TrimLeadingWhitespace(rest);
            if (rest.StartsWith("<?"u8))
            {
                var end = rest.IndexOf("?>"u8);
                if (end < 0)
                {
                    return false;
                }

                rest = rest[(end + 2)..];
                continue;
            }

            if (rest.StartsWith("<!--"u8))
            {
                var end = rest.IndexOf("-->"u8);
                if (end < 0)
                {
                    return false;
                }

                rest = rest[(end + 3)..];
                continue;
            }

            if (rest.StartsWith("<!DOCTYPE"u8))
            {
                // Skip past an internal subset ("[ ... ]>") when there is one; otherwise past the first ">".
                var close = rest.IndexOf((byte)'>');
                var subsetStart = rest.IndexOf((byte)'[');
                if (subsetStart >= 0 && (close < 0 || subsetStart < close))
                {
                    var subsetEnd = rest.IndexOf("]>"u8);
                    if (subsetEnd < 0)
                    {
                        return false;
                    }

                    rest = rest[(subsetEnd + 2)..];
                    continue;
                }

                if (close < 0)
                {
                    return false;
                }

                rest = rest[(close + 1)..];
                continue;
            }

            break;
        }

        if (!rest.StartsWith("<svg"u8))
        {
            return false;
        }

        return rest.Length == 4 || rest[4] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'>' or (byte)'/';
    }

    private static ReadOnlySpan<byte> TrimLeadingWhitespace(ReadOnlySpan<byte> span)
    {
        var index = 0;
        while (index < span.Length && span[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        return span[index..];
    }

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
