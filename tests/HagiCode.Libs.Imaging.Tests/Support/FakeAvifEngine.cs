using HagiCode.Libs.Imaging;
using HagiCode.Libs.Imaging.Engine;

namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>
/// Deterministic engine for unit tests. Counts calls, records the highest number of concurrent encodes, and can block
/// or fail on demand. Output is a minimal AVIF <c>ftyp</c> box so the output checks still apply.
/// </summary>
internal sealed class FakeAvifEngine : IAvifEngine
{
    private int _active;
    private int _maxActive;
    private int _headerCalls;
    private int _convertCalls;

    public SourceHeader Header { get; set; } = new(10, 10, HasAlpha: false);

    public Exception? HeaderException { get; set; }

    /// <summary>Time each encode takes, so parallel conversions overlap in the tests.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public Exception? ConvertException { get; set; }

    /// <summary>When set, <see cref="Convert"/> blocks until the event is signaled.</summary>
    public ManualResetEventSlim? Block { get; set; }

    public int HeaderCalls => Volatile.Read(ref _headerCalls);

    public int ConvertCalls => Volatile.Read(ref _convertCalls);

    public int MaxConcurrentConverts => Volatile.Read(ref _maxActive);

    public SourceHeader ReadHeader(byte[] source, AvifSourceFormat format)
    {
        Interlocked.Increment(ref _headerCalls);
        if (HeaderException is not null)
        {
            throw HeaderException;
        }

        return Header;
    }

    public EncodedImage Convert(byte[] source, AvifSourceFormat format, AvifConversionOptions options)
    {
        Interlocked.Increment(ref _convertCalls);
        var now = Interlocked.Increment(ref _active);
        InterlockedMax(ref _maxActive, now);
        try
        {
            Block?.Wait();
            if (Delay > TimeSpan.Zero)
            {
                Thread.Sleep(Delay);
            }

            if (ConvertException is not null)
            {
                throw ConvertException;
            }

            return new EncodedImage(FakeAvifBytes(), Header.Width, Header.Height, Header.HasAlpha);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    internal static byte[] FakeAvifBytes() =>
    [
        0x00, 0x00, 0x00, 0x14, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'a', (byte)'v', (byte)'i', (byte)'f', 0x00, 0x00, 0x00, 0x00,
        (byte)'a', (byte)'v', (byte)'i', (byte)'f',
    ];

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
