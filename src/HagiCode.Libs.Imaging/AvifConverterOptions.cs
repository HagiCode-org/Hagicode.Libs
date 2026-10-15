namespace HagiCode.Libs.Imaging;

/// <summary>
/// Process-wide limits and concurrency for an <see cref="AvifConverter"/>. Defaults are documented in the integration guide.
/// </summary>
public sealed class AvifConverterOptions
{
    /// <summary>Default input limit: 64 MiB.</summary>
    public const long DefaultMaxInputBytes = 64L * 1024 * 1024;

    /// <summary>Default decoded pixel limit: 64 million pixels.</summary>
    public const long DefaultMaxPixels = 64_000_000L;

    /// <summary>Largest source accepted, in bytes. Checked before and while reading.</summary>
    public long MaxInputBytes { get; set; } = DefaultMaxInputBytes;

    /// <summary>Largest decoded pixel count (width times height, after EXIF orientation) accepted. Checked from the header.</summary>
    public long MaxPixels { get; set; } = DefaultMaxPixels;

    /// <summary>How many conversions may encode at once. Default <c>max(1, ProcessorCount / 2)</c>.</summary>
    public int MaxConcurrentConversions { get; set; } = DefaultMaxConcurrentConversions();

    /// <summary>
    /// When true (default), the first converter in the process sets libvips' operation cache to zero entries and
    /// caps its thread count. Set to false if the host also uses libvips and wants to manage those settings itself.
    /// </summary>
    public bool ConfigureProcessWideEngineSettings { get; set; } = true;

    /// <summary>The default concurrency limit for this machine.</summary>
    public static int DefaultMaxConcurrentConversions() => Math.Max(1, Environment.ProcessorCount / 2);

    internal void Validate()
    {
        if (MaxInputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputBytes), MaxInputBytes, "MaxInputBytes must be greater than zero.");
        }

        if (MaxPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPixels), MaxPixels, "MaxPixels must be greater than zero.");
        }

        if (MaxConcurrentConversions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentConversions), MaxConcurrentConversions, "MaxConcurrentConversions must be at least 1.");
        }
    }

    /// <summary>Returns an independent copy, so later changes by the caller do not affect a converter.</summary>
    internal AvifConverterOptions Snapshot() => new()
    {
        MaxInputBytes = MaxInputBytes,
        MaxPixels = MaxPixels,
        MaxConcurrentConversions = MaxConcurrentConversions,
        ConfigureProcessWideEngineSettings = ConfigureProcessWideEngineSettings,
    };
}
