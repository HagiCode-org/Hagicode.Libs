using System.Diagnostics;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;
using Xunit;

namespace HagiCode.Libs.Imaging.Tests;

/// <summary>
/// Cancellation and resource behavior against the real engine. The memory bound is documented in
/// <c>AvifResourceBounds.md</c> next to this file.
/// </summary>
[Collection("NativeEngine")]
public class AvifResourceTests
{
    /// <summary>Largest working-set growth tolerated after several hundred conversions, measured after a forced GC.</summary>
    internal const long WorkingSetGrowthBoundBytes = 64L * 1024 * 1024;

    /// <summary>Largest managed-heap growth tolerated after a forced GC.</summary>
    internal const long ManagedGrowthBoundBytes = 16L * 1024 * 1024;

    [NativeAvifFact]
    public async Task Token_canceled_before_start_does_no_work_against_the_real_engine()
    {
        NativeRuntime.Require();
        using var converter = new AvifConverter(new AvifConverterOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            converter.ConvertAsync(Fixtures.Read("photo.png"), cancellationToken: cts.Token));
    }

    [NativeAvifFact]
    public async Task Cancel_while_encoding_ends_as_canceled_and_leaves_no_file()
    {
        NativeRuntime.Require();
        using var converter = new AvifConverter(new AvifConverterOptions());
        var directory = Fixtures.NewTempDirectory();
        var destination = Path.Combine(directory, "slow.avif");
        var big = NetVips.Image.Black(2000, 1500, bands: 3).PngsaveBuffer();
        var sourcePath = Path.Combine(directory, "big.png");
        File.WriteAllBytes(sourcePath, big);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            converter.ConvertFileAsync(sourcePath, destination, new AvifConversionOptions { Speed = 0 }, cancellationToken: cts.Token));

        File.Exists(destination).ShouldBeFalse();
        Directory.GetFiles(directory, "*.tmp").ShouldBeEmpty();
    }

    [NativeAvifFact]
    public async Task Several_hundred_conversions_and_failure_paths_stay_within_the_memory_bound()
    {
        NativeRuntime.Require();
        using var converter = new AvifConverter(new AvifConverterOptions());
        var photo = Fixtures.Read("photo.jpg");
        var corrupt = Fixtures.Read("truncated.png");
        var bomb = Fixtures.Read("bomb.png");

        // Warm up the allocator and thread pool before the baseline, so only steady-state growth is measured.
        for (var i = 0; i < 50; i++)
        {
            await converter.ConvertAsync(photo);
        }

        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
        var managedBefore = ForcedManagedBytes();

        for (var i = 0; i < 400; i++)
        {
            await converter.ConvertAsync(photo);
        }

        for (var i = 0; i < 100; i++)
        {
            await Should.ThrowAsync<AvifConversionException>(() => converter.ConvertAsync(corrupt));
            await Should.ThrowAsync<AvifConversionException>(() => converter.ConvertAsync(bomb));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => converter.ConvertAsync(photo, cancellationToken: cts.Token));
        }

        var managedGrowth = ForcedManagedBytes() - managedBefore;
        var workingSetGrowth = Process.GetCurrentProcess().WorkingSet64 - workingSetBefore;
        managedGrowth.ShouldBeLessThan(ManagedGrowthBoundBytes);
        workingSetGrowth.ShouldBeLessThan(WorkingSetGrowthBoundBytes);
    }

    [NativeAvifFact]
    public void Disposal_is_idempotent_with_the_real_engine()
    {
        NativeRuntime.Require();
        var converter = new AvifConverter(new AvifConverterOptions());

        converter.Dispose();
        converter.Dispose();
    }

    private static long ForcedManagedBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
