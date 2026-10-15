namespace HagiCode.Libs.Imaging;

/// <summary>The source format identified from the content of the bytes.</summary>
public enum AvifSourceFormat
{
    /// <summary>PNG.</summary>
    Png = 1,

    /// <summary>JPEG (JFIF or Exif).</summary>
    Jpeg = 2,

    /// <summary>WebP. The first frame is used.</summary>
    WebP = 3,

    /// <summary>GIF. The first frame is used.</summary>
    Gif = 4,

    /// <summary>AVIF. Decoded and re-encoded.</summary>
    Avif = 5,
}

/// <summary>Metadata of one successful conversion.</summary>
public sealed record AvifConversionResult
{
    /// <summary>Source format detected from the content.</summary>
    public AvifSourceFormat SourceFormat { get; init; }

    /// <summary>Source width after EXIF orientation, before any resize.</summary>
    public int SourceWidth { get; init; }

    /// <summary>Source height after EXIF orientation, before any resize.</summary>
    public int SourceHeight { get; init; }

    /// <summary>Output width in pixels.</summary>
    public int OutputWidth { get; init; }

    /// <summary>Output height in pixels.</summary>
    public int OutputHeight { get; init; }

    /// <summary>Number of AVIF bytes produced.</summary>
    public long OutputByteLength { get; init; }

    /// <summary>True when the output carries an alpha channel.</summary>
    public bool HasAlpha { get; init; }

    /// <summary>Wall-clock time the conversion took, including the wait for a slot.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>The AVIF bytes. Filled only by the buffer overload; empty for stream and file output.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
}
