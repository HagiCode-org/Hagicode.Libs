using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;
using Xunit;

namespace HagiCode.Libs.Imaging.Tests;

/// <summary>
/// Structure is read with the test-only ISO BMFF reader, not the engine. Fidelity is PSNR against the decoded source.
/// Thresholds are the gate G1 values from design D11 (quality 30 at least 28 dB, default 60 at least 33 dB, 90 at least 40 dB).
/// </summary>
[Collection("NativeEngine")]
public class AvifStructureAndFidelityTests
{
    private static readonly AvifConverter Converter = new(new AvifConverterOptions());

    public static IEnumerable<object[]> QualityThresholds => new[]
    {
        new object[] { 30, 28.0 },
        new object[] { 60, 33.0 },
        new object[] { 90, 40.0 },
    };

    [NativeAvifFact]
    public async Task Output_carries_the_avif_brand_and_declared_dimensions()
    {
        NativeRuntime.Require();

        var result = await Converter.ConvertAsync(Fixtures.Read("photo.png"));

        var file = result.Data.ToArray();
        IsoBmff.MajorBrand(file).ShouldBe("avif");
        IsoBmff.CompatibleBrands(file).ShouldContain("avif");
        IsoBmff.ImageSize(file).ShouldBe((result.OutputWidth, result.OutputHeight));
    }

    [NativeAvifTheory]
    [MemberData(nameof(QualityThresholds))]
    public async Task Fidelity_meets_the_documented_threshold_for_each_quality(int quality, double minimumPsnr)
    {
        NativeRuntime.Require();
        var source = Fixtures.Read("photo.png");
        var reference = Measure.DecodeRgb(source, AvifSourceFormat.Png).Rgb;

        var result = await Converter.ConvertAsync(source, new AvifConversionOptions { Quality = quality });

        var decoded = Measure.DecodeRgb(result.Data.ToArray(), AvifSourceFormat.Avif);
        decoded.Rgb.Length.ShouldBe(reference.Length);
        Measure.Psnr(reference, decoded.Rgb).ShouldBeGreaterThanOrEqualTo(minimumPsnr);
    }
}
