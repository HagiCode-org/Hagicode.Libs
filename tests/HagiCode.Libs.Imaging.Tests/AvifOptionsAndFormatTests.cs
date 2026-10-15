using System.Text;
using HagiCode.Libs.Imaging.Internal;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

public class AvifOptionsAndFormatTests
{
    [TestCase("photo.png", AvifSourceFormat.Png)]
    [TestCase("photo.jpg", AvifSourceFormat.Jpeg)]
    [TestCase("photo.webp", AvifSourceFormat.WebP)]
    [TestCase("anim.gif", AvifSourceFormat.Gif)]
    [TestCase("photo.avif", AvifSourceFormat.Avif)]
    public void Sniffer_identifies_each_supported_format_from_content(string file, AvifSourceFormat expected)
    {
        SourceSniffer.Detect(Fixtures.Read(file)).ShouldBe(expected);
    }

    [Test]
    public void Sniffer_ignores_the_file_extension_for_a_jpeg_named_png()
    {
        var bytes = Fixtures.Read("photo.jpg");
        var path = Path.Combine(Fixtures.NewTempDirectory(), "looks-like.png");
        File.WriteAllBytes(path, bytes);

        SourceSniffer.Detect(File.ReadAllBytes(path)).ShouldBe(AvifSourceFormat.Jpeg);
    }

    [TestCase("svg")]
    [TestCase("text")]
    [TestCase("empty")]
    [TestCase("truncated-png")]
    public void Sniffer_rejects_unsupported_or_too_short_content(string kind)
    {
        var bytes = kind switch
        {
            "svg" => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
            "text" => Encoding.UTF8.GetBytes("hello, not an image"),
            "empty" => Array.Empty<byte>(),
            _ => Fixtures.Read("photo.png")[..5],
        };

        SourceSniffer.Detect(bytes).ShouldBeNull();
    }

    [Test]
    public void Sniffer_and_signature_check_run_without_the_native_runtime()
    {
        // Pure managed code: no NetVips call is made on this path.
        AvifFormat.IsAvif(Fixtures.Read("photo.avif")).ShouldBeTrue();
        AvifFormat.IsAvif(Fixtures.Read("photo.jpg")).ShouldBeFalse();
    }

    [Test]
    public void IsAvif_accepts_avis_major_brand_and_compatible_avif_brand()
    {
        var avisMajor = Ftyp("avis", ["mif1"]);
        var compatibleAvif = Ftyp("mif1", ["miaf", "avif"]);

        AvifFormat.IsAvif(avisMajor).ShouldBeTrue();
        AvifFormat.IsAvif(compatibleAvif).ShouldBeTrue();
    }

    [Test]
    public void IsAvif_rejects_truncated_header_and_non_avif_ftyp()
    {
        AvifFormat.IsAvif(Fixtures.Read("photo.avif")[..11]).ShouldBeFalse();
        AvifFormat.IsAvif(Ftyp("heic", ["mif1"])).ShouldBeFalse();
    }

    [Test]
    public void Options_defaults_are_valid()
    {
        var options = new AvifConversionOptions();
        options.Quality.ShouldBe(60);
        options.Speed.ShouldBe(6);
        options.ChromaSubsampling.ShouldBe(AvifChromaSubsampling.Auto);
        options.Alpha.ShouldBe(AvifAlphaMode.Preserve);
        options.StripMetadata.ShouldBeTrue();
        Should.NotThrow(() => options.Validate());
    }

    [TestCase(101, 6)]
    [TestCase(-1, 6)]
    [TestCase(60, 11)]
    [TestCase(60, -1)]
    public void Quality_and_speed_out_of_range_are_invalid_options(int quality, int speed)
    {
        var error = Should.Throw<AvifConversionException>(() =>
            new AvifConversionOptions { Quality = quality, Speed = speed }.Validate());
        error.ErrorCode.ShouldBe(AvifErrorCode.InvalidOptions);
    }

    [TestCase(0, 100)]
    [TestCase(100, 0)]
    [TestCase(-5, 100)]
    public void Zero_size_resize_target_is_invalid_options(int width, int height)
    {
        var error = Should.Throw<AvifConversionException>(() =>
            new AvifConversionOptions { Resize = new AvifResize(AvifResizeMode.Fit, width, height) }.Validate());
        error.ErrorCode.ShouldBe(AvifErrorCode.InvalidOptions);
    }

    [TestCase("red")]
    [TestCase("#12345")]
    [TestCase("#zzzzzz")]
    [TestCase("64748b")]
    public void Flatten_color_must_be_hex_rrggbb(string color)
    {
        var error = Should.Throw<AvifConversionException>(() =>
            new AvifConversionOptions { Alpha = AvifAlphaMode.Flatten, FlattenColor = color }.Validate());
        error.ErrorCode.ShouldBe(AvifErrorCode.InvalidOptions);
    }

    [Test]
    public void Flatten_color_parses_to_channel_bytes()
    {
        AvifConversionOptions.ParseHexColor("#64748b").ShouldBe(((byte)0x64, (byte)0x74, (byte)0x8b));
    }

    private static byte[] Ftyp(string major, string[] compatible)
    {
        var bytes = new List<byte>();
        var size = 16 + compatible.Length * 4;
        bytes.AddRange(new byte[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size });
        bytes.AddRange(Encoding.ASCII.GetBytes("ftyp"));
        bytes.AddRange(Encoding.ASCII.GetBytes(major));
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        foreach (var brand in compatible)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(brand));
        }

        return bytes.ToArray();
    }
}
