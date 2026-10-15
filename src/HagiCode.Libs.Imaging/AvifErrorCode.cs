namespace HagiCode.Libs.Imaging;

/// <summary>
/// Stable error codes carried by <see cref="AvifConversionException"/>. The numeric values are part of the public
/// contract and must not be reused or renumbered.
/// </summary>
public enum AvifErrorCode
{
    /// <summary>An option value is out of range or inconsistent, or the source and destination are the same file.</summary>
    InvalidOptions = 1,

    /// <summary>The source bytes are not PNG, JPEG, WebP, GIF or AVIF.</summary>
    UnsupportedSourceFormat = 2,

    /// <summary>The source claims a supported format but cannot be decoded (for example a truncated file).</summary>
    CorruptSource = 3,

    /// <summary>The source is larger than <see cref="AvifConverterOptions.MaxInputBytes"/>.</summary>
    SourceTooLarge = 4,

    /// <summary>The declared pixel count is larger than <see cref="AvifConverterOptions.MaxPixels"/>.</summary>
    DimensionsTooLarge = 5,

    /// <summary>The destination exists and overwrite was not requested.</summary>
    DestinationExists = 6,

    /// <summary>The native imaging runtime cannot be used on this platform or its binaries cannot load.</summary>
    NativeRuntimeUnavailable = 7,

    /// <summary>The AVIF encoder failed to produce output.</summary>
    EncodeFailed = 8,
}
