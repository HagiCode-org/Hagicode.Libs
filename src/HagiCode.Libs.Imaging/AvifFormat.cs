namespace HagiCode.Libs.Imaging;

/// <summary>
/// Managed AVIF signature detection. Needs no native code, so it keeps working when the native runtime is unavailable.
/// Ported from the backend <c>AvifFileSignature</c> behavior.
/// </summary>
public static class AvifFormat
{
    /// <summary>
    /// Returns true when the header begins with an <c>ftyp</c> box that names the <c>avif</c> or <c>avis</c> brand,
    /// either as the major brand or as a compatible brand within the box.
    /// </summary>
    public static bool IsAvif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || !header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        if (IsAvifBrand(header.Slice(8, 4)))
        {
            return true;
        }

        var boxSize = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        var limit = Math.Min(Math.Max(boxSize, 0), header.Length);
        for (var offset = 16; offset + 4 <= limit; offset += 4)
        {
            if (IsAvifBrand(header.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAvifBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);
}
