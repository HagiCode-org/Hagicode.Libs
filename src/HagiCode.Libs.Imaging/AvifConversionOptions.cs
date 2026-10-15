namespace HagiCode.Libs.Imaging;

/// <summary>Chroma subsampling requested for the AVIF output.</summary>
public enum AvifChromaSubsampling
{
    /// <summary>Let the encoder choose. This is the default.</summary>
    Auto = 0,

    /// <summary>4:2:0 chroma subsampling.</summary>
    Yuv420 = 1,

    /// <summary>4:4:4 chroma with no subsampling.</summary>
    Yuv444 = 2,
}

/// <summary>Whether source transparency is kept or composited onto a background color.</summary>
public enum AvifAlphaMode
{
    /// <summary>Keep the alpha channel. This is the default.</summary>
    Preserve = 0,

    /// <summary>Composite onto <see cref="AvifConversionOptions.FlattenColor"/> and drop the alpha channel.</summary>
    Flatten = 1,
}

/// <summary>How the source is resized to the target box.</summary>
public enum AvifResizeMode
{
    /// <summary>Keep the source size.</summary>
    None = 0,

    /// <summary>Preserve the aspect ratio inside the box. Never enlarges.</summary>
    Fit = 1,

    /// <summary>Fill the box exactly, scaling as needed and cropping the overflow around the center.</summary>
    Cover = 2,
}

/// <summary>A resize request. Width and height must be greater than zero.</summary>
/// <param name="Mode">The resize mode.</param>
/// <param name="Width">Target box width in pixels.</param>
/// <param name="Height">Target box height in pixels.</param>
public sealed record AvifResize(AvifResizeMode Mode, int Width, int Height);

/// <summary>Per-call conversion settings. Defaults match the HeroAvatar and thumbnail measurements from gate G1.</summary>
public sealed class AvifConversionOptions
{
    /// <summary>Default quality measured in gate G1 (1200x776 photograph: 38.4 dB PSNR, 63 KB).</summary>
    public const int DefaultQuality = 60;

    /// <summary>Default speed, matching the current ffmpeg <c>cpu-used 6</c> setting.</summary>
    public const int DefaultSpeed = 6;

    /// <summary>Default flatten color, used only when <see cref="Alpha"/> is <see cref="AvifAlphaMode.Flatten"/>.</summary>
    public const string DefaultFlattenColor = "#ffffff";

    /// <summary>Encoder quality, 0 (smallest) to 100 (largest, closest to the source). Default 60.</summary>
    public int Quality { get; init; } = DefaultQuality;

    /// <summary>Encoder speed, 0 (slowest, most thorough) to 10 (fastest). Default 6.</summary>
    public int Speed { get; init; } = DefaultSpeed;

    /// <summary>Chroma subsampling. Default <see cref="AvifChromaSubsampling.Auto"/>.</summary>
    public AvifChromaSubsampling ChromaSubsampling { get; init; } = AvifChromaSubsampling.Auto;

    /// <summary>Alpha handling. Default <see cref="AvifAlphaMode.Preserve"/>.</summary>
    public AvifAlphaMode Alpha { get; init; } = AvifAlphaMode.Preserve;

    /// <summary>Background color in <c>#rrggbb</c> form, used when <see cref="Alpha"/> is Flatten.</summary>
    public string FlattenColor { get; init; } = DefaultFlattenColor;

    /// <summary>Optional resize. <c>null</c> keeps the source size.</summary>
    public AvifResize? Resize { get; init; }

    /// <summary>Remove EXIF, GPS and other embedded metadata. Default true. EXIF orientation is always applied to the pixels.</summary>
    public bool StripMetadata { get; init; } = true;

    /// <summary>Throws <see cref="AvifErrorCode.InvalidOptions"/> when a value is out of range. Runs before any decoding.</summary>
    internal void Validate()
    {
        if (Quality is < 0 or > 100)
        {
            throw Invalid($"Quality must be between 0 and 100 (was {Quality}).");
        }

        if (Speed is < 0 or > 10)
        {
            throw Invalid($"Speed must be between 0 and 10 (was {Speed}).");
        }

        if (!Enum.IsDefined(ChromaSubsampling))
        {
            throw Invalid("ChromaSubsampling is not a defined value.");
        }

        if (!Enum.IsDefined(Alpha))
        {
            throw Invalid("Alpha is not a defined value.");
        }

        if (Resize is { } resize)
        {
            if (!Enum.IsDefined(resize.Mode))
            {
                throw Invalid("Resize mode is not a defined value.");
            }

            if (resize.Width <= 0 || resize.Height <= 0)
            {
                throw Invalid($"Resize width and height must be greater than zero (was {resize.Width}x{resize.Height}).");
            }
        }

        _ = ParseHexColor(FlattenColor);
    }

    /// <summary>Parses <c>#rrggbb</c> into its channel bytes.</summary>
    internal static (byte R, byte G, byte B) ParseHexColor(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#' || !AllHex(value.AsSpan(1)))
        {
            throw Invalid("FlattenColor must be a color in #rrggbb form.");
        }

        return (
            Convert.ToByte(value.Substring(1, 2), 16),
            Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16));
    }

    private static bool AllHex(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static AvifConversionException Invalid(string message) =>
        new(AvifErrorCode.InvalidOptions, message);
}
