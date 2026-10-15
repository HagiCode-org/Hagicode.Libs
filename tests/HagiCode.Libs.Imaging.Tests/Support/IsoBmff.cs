using System.Buffers.Binary;
using System.Text;

namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>
/// Test-only ISO BMFF reader. It reads the boxes the structure tests assert on (<c>ftyp</c>, <c>ispe</c>, <c>av1C</c>,
/// <c>auxC</c>) directly from the file bytes, without the conversion engine, so a shared engine bug cannot hide.
/// </summary>
internal static class IsoBmff
{
    internal readonly record struct Box(string Type, long Offset, long Size, long PayloadOffset);

    internal static string MajorBrand(byte[] file) => Encoding.ASCII.GetString(file, 8, 4);

    internal static IReadOnlyList<string> CompatibleBrands(byte[] file)
    {
        var box = Find(file, "ftyp")!.Value;
        var brands = new List<string>();
        for (var offset = box.PayloadOffset + 8; offset + 4 <= box.Offset + box.Size; offset += 4)
        {
            brands.Add(Encoding.ASCII.GetString(file, (int)offset, 4));
        }

        return brands;
    }

    /// <summary>Returns the width and height declared by the first <c>ispe</c> box.</summary>
    internal static (int Width, int Height) ImageSize(byte[] file)
    {
        var box = Find(file, "ispe")!.Value;
        var payload = (int)box.PayloadOffset + 4;
        return (
            (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(payload, 4)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(payload + 4, 4)));
    }

    /// <summary>Returns the AV1 chroma subsampling declared in <c>av1C</c>: (x, y) is (1, 1) for 4:2:0 and (0, 0) for 4:4:4.</summary>
    internal static (int X, int Y) ChromaSubsampling(byte[] file)
    {
        var box = Find(file, "av1C")!.Value;
        var flags = file[box.PayloadOffset + 2];
        return ((flags >> 3) & 1, (flags >> 2) & 1);
    }

    /// <summary>True when an <c>auxC</c> box names the alpha auxiliary type.</summary>
    internal static bool HasAlphaAuxiliary(byte[] file)
    {
        var box = Find(file, "auxC");
        if (box is null)
        {
            return false;
        }

        var urn = Encoding.ASCII.GetString(file, (int)box.Value.PayloadOffset + 4, (int)(box.Value.Offset + box.Value.Size - box.Value.PayloadOffset - 4));
        return urn.Contains("auxiliary:alpha", StringComparison.Ordinal);
    }

    internal static Box? Find(byte[] file, string type) => Find(file, type, 0, file.Length);

    private static Box? Find(byte[] file, string type, long start, long end)
    {
        foreach (var box in Children(file, start, end))
        {
            if (box.Type == type)
            {
                return box;
            }

            if (box.Type is "meta" or "iprp" or "ipco")
            {
                var childStart = box.Type == "meta" ? box.PayloadOffset + 4 : box.PayloadOffset;
                var found = Find(file, type, childStart, box.Offset + box.Size);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static IEnumerable<Box> Children(byte[] file, long start, long end)
    {
        var position = start;
        while (position + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)position, 4));
            var type = Encoding.ASCII.GetString(file, (int)position + 4, 4);
            long header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan((int)position + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < header || position + size > end)
            {
                yield break;
            }

            yield return new Box(type, position, size, position + header);
            position += size;
        }
    }
}
