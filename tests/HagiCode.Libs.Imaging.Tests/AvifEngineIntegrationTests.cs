using System.Diagnostics;
using System.Text;
using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Tests.Support;
using NetVips;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

/// <summary>Real-engine checks for the source formats, options, resize, orientation and metadata behavior.</summary>
[NonParallelizable]
public class AvifEngineIntegrationTests
{
    private static readonly AvifConverter Converter = new(new AvifConverterOptions());

    public static IEnumerable<object[]> SupportedSources => new[]
    {
        new object[] { "photo.png", AvifSourceFormat.Png },
        new object[] { "photo.jpg", AvifSourceFormat.Jpeg },
        new object[] { "photo.webp", AvifSourceFormat.WebP },
        new object[] { "anim.gif", AvifSourceFormat.Gif },
        new object[] { "photo.avif", AvifSourceFormat.Avif },
    };

    [NativeAvifGate]
    [TestCaseSource(nameof(SupportedSources))]
    public async Task Every_supported_source_converts_to_a_valid_avif(string file, AvifSourceFormat format)
    {
        NativeRuntime.Require();

        var result = await Converter.ConvertAsync(Fixtures.Read(file));

        result.SourceFormat.ShouldBe(format);
        result.Data.Length.ShouldBe((int)result.OutputByteLength);
        IsoBmff.MajorBrand(result.Data.ToArray()).ShouldBe("avif");
    }

    [Test, NativeAvifGate]
    public async Task Animated_gif_yields_a_single_still_frame_from_the_first_frame()
    {
        NativeRuntime.Require();
        var gifFrameOne = Image.GifloadBuffer(Fixtures.Read("anim.gif"));
        var result = await Converter.ConvertAsync(Fixtures.Read("anim.gif"));

        var file = result.Data.ToArray();
        IsoBmff.ImageSize(file).ShouldBe((gifFrameOne.Width, gifFrameOne.Height));
        IsoBmff.MajorBrand(file).ShouldBe("avif");
        Encoding.ASCII.GetString(file).Contains("msf1", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Test, NativeAvifGate]
    public async Task Exif_orientation_six_swaps_dimensions_and_reports_upright_source_size()
    {
        NativeRuntime.Require();

        var result = await Converter.ConvertAsync(Fixtures.Read("exif6.jpg"));

        result.SourceWidth.ShouldBe(800);
        result.SourceHeight.ShouldBe(600);
        result.OutputWidth.ShouldBe(800);
        result.OutputHeight.ShouldBe(600);
    }

    [Test, NativeAvifGate]
    public async Task Exif_orientation_six_with_fit_200_gives_200_by_150()
    {
        NativeRuntime.Require();

        var result = await Converter.ConvertAsync(
            Fixtures.Read("exif6.jpg"),
            new AvifConversionOptions { Resize = new AvifResize(AvifResizeMode.Fit, 200, 200) });

        result.SourceWidth.ShouldBe(800);
        result.SourceHeight.ShouldBe(600);
        (result.OutputWidth, result.OutputHeight).ShouldBe((200, 150));
    }

    [Test, NativeAvifGate]
    public async Task Fit_2000_by_1000_into_500_box_gives_500_by_250()
    {
        NativeRuntime.Require();
        var png = Image.Black(2000, 1000, bands: 3).PngsaveBuffer();

        var result = await Converter.ConvertAsync(png, new AvifConversionOptions { Resize = new AvifResize(AvifResizeMode.Fit, 500, 500) });

        (result.OutputWidth, result.OutputHeight).ShouldBe((500, 250));
    }

    [Test, NativeAvifGate]
    public async Task Fit_never_enlarges_a_100_by_100_source()
    {
        NativeRuntime.Require();
        var png = Image.Black(100, 100, bands: 3).PngsaveBuffer();

        var result = await Converter.ConvertAsync(png, new AvifConversionOptions { Resize = new AvifResize(AvifResizeMode.Fit, 500, 500) });

        (result.OutputWidth, result.OutputHeight).ShouldBe((100, 100));
    }

    [Test, NativeAvifGate]
    public async Task Cover_300_by_100_into_128_box_is_exactly_128_square()
    {
        NativeRuntime.Require();
        var png = Image.Black(300, 100, bands: 3).PngsaveBuffer();

        var result = await Converter.ConvertAsync(png, new AvifConversionOptions { Resize = new AvifResize(AvifResizeMode.Cover, 128, 128) });

        (result.OutputWidth, result.OutputHeight).ShouldBe((128, 128));
        IsoBmff.ImageSize(result.Data.ToArray()).ShouldBe((128, 128));
    }

    [Test, NativeAvifGate]
    public async Task Gps_metadata_is_absent_by_default_and_kept_only_on_request()
    {
        NativeRuntime.Require();
        var source = Fixtures.Read("gps.jpg");

        var stripped = await Converter.ConvertAsync(source);
        var kept = await Converter.ConvertAsync(source, new AvifConversionOptions { StripMetadata = false });

        Encoding.ASCII.GetString(stripped.Data.ToArray()).ShouldNotContain("TestCam");
        Encoding.ASCII.GetString(kept.Data.ToArray()).ShouldContain("TestCam");
    }

    [Test, NativeAvifGate]
    public async Task Alpha_is_preserved_by_default()
    {
        NativeRuntime.Require();

        var result = await Converter.ConvertAsync(Fixtures.Read("alpha.png"));

        result.HasAlpha.ShouldBeTrue();
        var decoded = Measure.DecodeRgb(result.Data.ToArray(), AvifSourceFormat.Avif);
        decoded.Bands.ShouldBe(4);
        IsoBmff.HasAlphaAuxiliary(result.Data.ToArray()).ShouldBeTrue();
    }

    [Test, NativeAvifGate]
    public async Task Alpha_flattened_onto_slate_gives_no_alpha_and_transparent_pixels_take_the_color()
    {
        NativeRuntime.Require();

        var result = await Converter.ConvertAsync(
            Fixtures.Read("alpha.png"),
            new AvifConversionOptions { Alpha = AvifAlphaMode.Flatten, FlattenColor = "#64748b" });

        var file = result.Data.ToArray();
        result.HasAlpha.ShouldBeFalse();
        IsoBmff.HasAlphaAuxiliary(file).ShouldBeFalse();
        var decoded = Measure.DecodeRgb(file, AvifSourceFormat.Avif);
        var offset = (decoded.Width / 16 + decoded.Height / 2 * decoded.Width) * 3;
        decoded.Rgb[offset].ShouldBeInRange((byte)97, (byte)103);
        decoded.Rgb[offset + 1].ShouldBeInRange((byte)113, (byte)119);
        decoded.Rgb[offset + 2].ShouldBeInRange((byte)136, (byte)142);
    }

    [Test, NativeAvifGate]
    public async Task Higher_quality_is_larger_and_closer_to_the_source()
    {
        NativeRuntime.Require();
        var source = Fixtures.Read("photo.jpg");
        var reference = Measure.DecodeRgb(source, AvifSourceFormat.Jpeg).Rgb;

        var low = await Converter.ConvertAsync(source, new AvifConversionOptions { Quality = 30 });
        var high = await Converter.ConvertAsync(source, new AvifConversionOptions { Quality = 90 });

        high.OutputByteLength.ShouldBeGreaterThan(low.OutputByteLength);
        var psnrLow = Measure.Psnr(reference, Measure.DecodeRgb(low.Data.ToArray(), AvifSourceFormat.Avif).Rgb);
        var psnrHigh = Measure.Psnr(reference, Measure.DecodeRgb(high.Data.ToArray(), AvifSourceFormat.Avif).Rgb);
        psnrHigh.ShouldBeGreaterThanOrEqualTo(psnrLow);
    }

    [Test, NativeAvifGate]
    public async Task Speed_zero_and_speed_ten_both_produce_valid_output_of_the_same_size()
    {
        NativeRuntime.Require();
        var source = Fixtures.Read("photo.png");

        var slow = await Converter.ConvertAsync(source, new AvifConversionOptions { Speed = 0 });
        var fast = await Converter.ConvertAsync(source, new AvifConversionOptions { Speed = 10 });

        IsoBmff.MajorBrand(slow.Data.ToArray()).ShouldBe("avif");
        IsoBmff.MajorBrand(fast.Data.ToArray()).ShouldBe("avif");
        (slow.OutputWidth, slow.OutputHeight).ShouldBe((fast.OutputWidth, fast.OutputHeight));
    }

    [Test, NativeAvifGate]
    public async Task Truncated_png_is_reported_as_corrupt_source_with_no_output()
    {
        NativeRuntime.Require();
        var destination = Path.Combine(Fixtures.NewTempDirectory(), "truncated.avif");

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            Converter.ConvertFileAsync(Fixtures.PathOf("truncated.png"), destination));

        error.ErrorCode.ShouldBe(AvifErrorCode.CorruptSource);
        File.Exists(destination).ShouldBeFalse();
    }

    [Test, NativeAvifGate]
    public async Task Decompression_bomb_fails_from_the_header_without_a_pixel_buffer()
    {
        NativeRuntime.Require();
        var before = Process.GetCurrentProcess().WorkingSet64;

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            Converter.ConvertAsync(Fixtures.Read("bomb.png")));

        error.ErrorCode.ShouldBe(AvifErrorCode.DimensionsTooLarge);
        var growth = Process.GetCurrentProcess().WorkingSet64 - before;
        // The declared pixel buffer would be 4.8 GB. Allow generous slack for the runtime itself.
        growth.ShouldBeLessThan(256L * 1024 * 1024);
    }

    [Test]
    public async Task Svg_and_text_are_rejected_as_unsupported_by_the_real_converter()
    {
        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            Converter.ConvertAsync(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>")));

        error.ErrorCode.ShouldBe(AvifErrorCode.UnsupportedSourceFormat);
    }

    [Test, NativeAvifGate]
    public async Task Chroma_request_is_reflected_in_the_av1_configuration()
    {
        NativeRuntime.Require();
        var source = Fixtures.Read("photo.png");

        var yuv420 = await Converter.ConvertAsync(source, new AvifConversionOptions { ChromaSubsampling = AvifChromaSubsampling.Yuv420 });
        var yuv444 = await Converter.ConvertAsync(source, new AvifConversionOptions { ChromaSubsampling = AvifChromaSubsampling.Yuv444 });

        IsoBmff.ChromaSubsampling(yuv420.Data.ToArray()).ShouldBe((1, 1));
        IsoBmff.ChromaSubsampling(yuv444.Data.ToArray()).ShouldBe((0, 0));
    }
}
