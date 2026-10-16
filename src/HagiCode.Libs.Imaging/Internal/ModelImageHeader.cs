namespace HagiCode.Libs.Imaging.Internal;

/// <summary>
/// Reads width and height from the header of a PNG, JPEG, GIF or WebP file without a native library. Used so that
/// pass-through decisions and the header-first pixel check work even when the native runtime is unavailable.
/// Returns false for any header it cannot read; the caller then decides how to proceed.
/// </summary>
internal static class ModelImageHeader
{
    internal static bool TryRead(ReadOnlySpan<byte> bytes, AvifSourceFormat format, out int width, out int height)
    {
        width = 0;
        height = 0;
        return format switch
        {
            AvifSourceFormat.Png => TryReadPng(bytes, out width, out height),
            AvifSourceFormat.Gif => TryReadGif(bytes, out width, out height),
            AvifSourceFormat.Jpeg => TryReadJpeg(bytes, out width, out height),
            AvifSourceFormat.WebP => TryReadWebP(bytes, out width, out height),
            _ => false,
        };
    }

    private static bool TryReadPng(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 24 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = ReadUInt32BigEndian(bytes, 16);
        height = ReadUInt32BigEndian(bytes, 20);
        return width > 0 && height > 0;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 10)
        {
            return false;
        }

        width = bytes[6] | (bytes[7] << 8);
        height = bytes[8] | (bytes[9] << 8);
        return width > 0 && height > 0;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return false;
        }

        var position = 2;
        while (position + 4 <= bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                return false;
            }

            var marker = bytes[position + 1];
            if (marker == 0xFF)
            {
                // Fill byte before a marker.
                position++;
                continue;
            }

            // Standalone markers carry no length: TEM, RSTn, SOI, EOI.
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
            {
                position += 2;
                continue;
            }

            var length = (bytes[position + 2] << 8) | bytes[position + 3];
            if (length < 2)
            {
                return false;
            }

            // SOF0..SOF15 carry the frame size, except DHT (C4), JPG (C8) and DAC (CC).
            var isStartOfFrame = marker >= 0xC0 && marker <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isStartOfFrame)
            {
                if (position + 9 > bytes.Length)
                {
                    return false;
                }

                height = (bytes[position + 5] << 8) | bytes[position + 6];
                width = (bytes[position + 7] << 8) | bytes[position + 8];
                return width > 0 && height > 0;
            }

            position += 2 + length;
        }

        return false;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 30 || !bytes.Slice(12, 4).SequenceEqual("VP8 "u8) && !bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && !bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
        {
            return false;
        }

        if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
        {
            // Canvas size: 24-bit little-endian values stored minus one.
            width = (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
            height = (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1;
            return true;
        }

        if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8))
        {
            if (bytes[20] != 0x2F)
            {
                return false;
            }

            var bits = bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24);
            width = (bits & 0x3FFF) + 1;
            height = ((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        // Lossy VP8 keyframe: start code 9D 01 2A at 23..25, then 14-bit width and height at 26 and 28.
        if (bytes[23] != 0x9D || bytes[24] != 0x01 || bytes[25] != 0x2A)
        {
            return false;
        }

        width = (bytes[26] | (bytes[27] << 8)) & 0x3FFF;
        height = (bytes[28] | (bytes[29] << 8)) & 0x3FFF;
        return width > 0 && height > 0;
    }

    private static int ReadUInt32BigEndian(ReadOnlySpan<byte> bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}
