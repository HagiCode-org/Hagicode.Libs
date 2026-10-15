using System.Reflection;
using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

/// <summary>Engine wrapper that counts how many encodes overlap, using the real libvips engine underneath.</summary>
internal sealed class ObservingEngine(IAvifEngine inner) : IAvifEngine
{
    private int _active;
    private int _max;

    public int MaxActive => Volatile.Read(ref _max);

    public SourceHeader ReadHeader(byte[] source, AvifSourceFormat format) => inner.ReadHeader(source, format);

    public EncodedImage Convert(byte[] source, AvifSourceFormat format, AvifConversionOptions options)
    {
        var now = Interlocked.Increment(ref _active);
        int seen;
        do
        {
            seen = Volatile.Read(ref _max);
            if (now <= seen)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _max, now, seen) != seen);

        try
        {
            return inner.Convert(source, format, options);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

/// <summary>Collection that runs without parallel tests, so environment changes cannot leak into other tests.</summary>
[NonParallelizable]
public class AvifConcurrencyTests
{
    [Test, NativeAvifGate]
    public async Task Sixteen_parallel_real_encodes_with_a_limit_of_two_never_overlap_more_than_two()
    {
        NativeRuntime.Require();
        var observer = new ObservingEngine(new LibvipsAvifEngine());
        using var converter = new AvifConverter(
            new AvifConverterOptions { MaxConcurrentConversions = 2, ConfigureProcessWideEngineSettings = false },
            observer,
            AvifRuntime.Probe);
        var source = Fixtures.Read("photo.png");

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            converter.ConvertAsync(source, new AvifConversionOptions { Speed = 10 })));

        results.Length.ShouldBe(16);
        results.ShouldAllBe(r => r.Data.Length > 0);
        observer.MaxActive.ShouldBeLessThanOrEqualTo(2);
        observer.MaxActive.ShouldBeGreaterThanOrEqualTo(1);
    }
}

[NonParallelizable]
public class AvifInProcessGuardTests
{
    [Test]
    public void Imaging_assembly_has_no_reference_to_process_or_process_start_info()
    {
        var hits = ProcessApiScanner.FindProcessReferences(typeof(HagiCode.Libs.Imaging.AvifConverter).Assembly.Location);

        hits.ShouldBeEmpty();
    }

    [Test]
    public void The_scanner_detects_a_sample_type_that_does_reference_process_apis()
    {
        var hits = ProcessApiScanner.FindProcessReferences(typeof(ProcessGuardSample).Assembly.Location);

        hits.ShouldContain("System.Diagnostics.ProcessStartInfo");
    }

    [Test, NativeAvifGate]
    public async Task Every_supported_format_converts_with_an_empty_path_and_no_executables()
    {
        NativeRuntime.Require();
        var emptyDirectory = Fixtures.NewTempDirectory();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyDirectory);
            using var converter = new AvifConverter(new AvifConverterOptions());
            foreach (var file in new[] { "photo.png", "photo.jpg", "photo.webp", "anim.gif", "photo.avif" })
            {
                var result = await converter.ConvertAsync(Fixtures.Read(file));
                result.OutputByteLength.ShouldBeGreaterThan(0);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
