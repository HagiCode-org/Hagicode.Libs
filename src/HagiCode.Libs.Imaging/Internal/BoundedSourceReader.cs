using System.Buffers;

namespace HagiCode.Libs.Imaging.Internal;

/// <summary>
/// Reads a source with a hard byte limit. Never keeps more than <c>maxBytes</c> in memory: the limit is checked before
/// each chunk is appended, so an oversize stream fails after at most one extra chunk has been read.
/// </summary>
internal static class BoundedSourceReader
{
    internal const int ChunkSize = 81920;

    /// <summary>Checks a buffer length against the limit.</summary>
    internal static void EnsureBufferWithinLimit(long length, long maxBytes)
    {
        if (length > maxBytes)
        {
            throw Oversize(maxBytes);
        }
    }

    /// <summary>Reads a stream into a buffer. The stream is not closed or disposed.</summary>
    internal static async Task<byte[]> ReadAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.CanSeek)
        {
            EnsureBufferWithinLimit(source.Length - source.Position, maxBytes);
        }

        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using var collected = source.CanSeek ? new MemoryStream((int)Math.Min(source.Length, maxBytes)) : new MemoryStream();
            while (true)
            {
                var read = await source.ReadAsync(chunk.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (collected.Length + read > maxBytes)
                {
                    throw Oversize(maxBytes);
                }

                collected.Write(chunk, 0, read);
            }

            return collected.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static AvifConversionException Oversize(long maxBytes) =>
        new(AvifErrorCode.SourceTooLarge, $"The source is larger than the {maxBytes}-byte input limit.");
}
