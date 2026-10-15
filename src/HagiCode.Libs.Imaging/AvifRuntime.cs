using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Internal;
using NetVips;

namespace HagiCode.Libs.Imaging;

/// <summary>Result of the runtime capability probe.</summary>
/// <param name="RuntimeIdentifier">The detected runtime identifier, for example <c>linux-x64</c>.</param>
/// <param name="EncodingAvailable">True when the encoder and every required loader passed the self-test.</param>
/// <param name="DecodableFormats">The source formats the runtime decodes. Empty when unavailable.</param>
/// <param name="UnavailableReason">Why the runtime is unavailable, or <c>null</c> when it is available.</param>
public sealed record AvifRuntimeStatus(
    string RuntimeIdentifier,
    bool EncodingAvailable,
    IReadOnlyList<AvifSourceFormat> DecodableFormats,
    string? UnavailableReason)
{
    /// <summary>True when AVIF encoding is available on this machine.</summary>
    public bool IsAvailable => EncodingAvailable && UnavailableReason is null;
}

/// <summary>
/// Capability probe for the native imaging runtime. <see cref="Probe"/> runs a real self-test once per process: it
/// encodes a tiny image and decodes five embedded fixtures. The result is cached, thread-safe, and never throws.
/// </summary>
public static class AvifRuntime
{
    private static readonly object SettingsLock = new();
    private static bool _cacheConfigured;
    private static bool _concurrencyConfigured;
    private static readonly Lazy<AvifRuntimeStatus> ProbeResult = new(RunProbe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Returns the cached probe result, running the self-test on the first call.</summary>
    public static AvifRuntimeStatus Probe() => ProbeResult.Value;

    /// <summary>
    /// Applies the process-wide libvips settings once: the operation cache is set to zero entries, untrusted operations
    /// are blocked, and the engine thread count is capped to <c>ProcessorCount / maxConcurrentConversions</c>. Later
    /// calls keep the first values.
    /// </summary>
    internal static void ConfigureProcessWideSettings(int maxConcurrentConversions)
    {
        lock (SettingsLock)
        {
            if (!_cacheConfigured)
            {
                Cache.Max = 0;
                // Untrusted operations (delegate loaders for formats outside the allow-list) are refused by libvips.
                NetVips.NetVips.BlockUntrusted = true;
                _cacheConfigured = true;
            }

            if (!_concurrencyConfigured)
            {
                NetVips.NetVips.Concurrency = Math.Max(1, Environment.ProcessorCount / Math.Max(1, maxConcurrentConversions));
                _concurrencyConfigured = true;
            }
        }
    }

    private static AvifRuntimeStatus RunProbe()
    {
        var runtimeIdentifier = NativePlatform.DetectRuntimeIdentifier();
        if (!NativePlatform.IsSupported(runtimeIdentifier))
        {
            return Unavailable(runtimeIdentifier, $"'{runtimeIdentifier}' is not one of the supported runtime identifiers ({NativePlatform.SupportedList}).");
        }

        try
        {
            using (var tiny = Image.Black(8, 8, bands: 3))
            {
                var encoded = tiny.HeifsaveBuffer(
                    q: 50,
                    compression: Enums.ForeignHeifCompression.Av1,
                    effort: 0,
                    keep: Enums.ForeignKeep.None);
                if (!AvifFormat.IsAvif(encoded))
                {
                    return Unavailable(runtimeIdentifier, "The encoder produced output without an AVIF signature.");
                }
            }

            var fixtures = new (AvifSourceFormat Format, byte[] Bytes)[]
            {
                (AvifSourceFormat.Png, ProbeFixtures.Png),
                (AvifSourceFormat.Jpeg, ProbeFixtures.Jpeg),
                (AvifSourceFormat.WebP, ProbeFixtures.WebP),
                (AvifSourceFormat.Gif, ProbeFixtures.Gif),
                (AvifSourceFormat.Avif, ProbeFixtures.Avif),
            };

            foreach (var (format, bytes) in fixtures)
            {
                using var decoded = LibvipsAvifEngine.Load(bytes, format, null);
                if (decoded.Width != 8 || decoded.Height != 8)
                {
                    return Unavailable(runtimeIdentifier, $"The {format} fixture decoded to an unexpected size.");
                }
            }

            return new AvifRuntimeStatus(
                runtimeIdentifier,
                EncodingAvailable: true,
                DecodableFormats: [AvifSourceFormat.Png, AvifSourceFormat.Jpeg, AvifSourceFormat.WebP, AvifSourceFormat.Gif, AvifSourceFormat.Avif],
                UnavailableReason: null);
        }
        catch (Exception exception)
        {
            return Unavailable(runtimeIdentifier, Describe(exception));
        }
    }

    private static AvifRuntimeStatus Unavailable(string runtimeIdentifier, string reason) =>
        new(runtimeIdentifier, EncodingAvailable: false, DecodableFormats: Array.Empty<AvifSourceFormat>(), UnavailableReason: reason);

    private static string Describe(Exception exception)
    {
        var root = exception;
        while (root is TypeInitializationException { InnerException: { } inner })
        {
            root = inner;
        }

        var firstLine = root.Message.Split('\n', 2)[0].Trim();
        return $"{root.GetType().Name}: {firstLine}";
    }
}
