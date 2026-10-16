using System.Text;
using HagiCode.Libs.Imaging.Internal;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

/// <summary>Content-based SVG detection and the managed header reader used by the model-image preparer.</summary>
public class ModelImageSnifferAndHeaderTests
{
    public static IEnumerable<object[]> SvgHeads => new[]
    {
        new object[] { "<svg xmlns='http://www.w3.org/2000/svg'/>" },
        new object[] { "﻿<svg width='1'/>" },
        new object[] { "<?xml version='1.0'?>\n<!-- generated -->\n<svg></svg>" },
        new object[] { "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'x.dtd'>\n<svg></svg>" },
        new object[] { "  \r\n\t<svg\n></svg>" },
    };

    [TestCaseSource(nameof(SvgHeads))]
    public void Svg_documents_are_detected_by_content(string text)
    {
        SourceSniffer.IsSvg(Encoding.UTF8.GetBytes(text)).ShouldBeTrue();
    }

    [TestCase("<html><svg></svg></html>")]
    [TestCase("<svgfoo></svgfoo>")]
    [TestCase("<!-- never closed")]
    [TestCase("plain text")]
    [TestCase("")]
    public void Non_svg_content_is_not_detected_as_svg(string text)
    {
        SourceSniffer.IsSvg(Encoding.UTF8.GetBytes(text)).ShouldBeFalse();
    }

    [Test]
    public void Raster_signatures_are_still_detected_and_not_svg()
    {
        var png = Fixtures.Read("photo.png");
        var jpeg = Fixtures.Read("photo.jpg");
        var gif = Fixtures.Read("anim.gif");
        var webp = Fixtures.Read("photo.webp");

        SourceSniffer.Detect(png).ShouldBe(AvifSourceFormat.Png);
        SourceSniffer.Detect(jpeg).ShouldBe(AvifSourceFormat.Jpeg);
        SourceSniffer.Detect(gif).ShouldBe(AvifSourceFormat.Gif);
        SourceSniffer.Detect(webp).ShouldBe(AvifSourceFormat.WebP);
        SourceSniffer.IsSvg(png).ShouldBeFalse();
        SourceSniffer.IsSvg(jpeg).ShouldBeFalse();
    }

    [TestCase("photo.png", AvifSourceFormat.Png, 320, 207)]
    [TestCase("photo.jpg", AvifSourceFormat.Jpeg, 320, 207)]
    [TestCase("anim.gif", AvifSourceFormat.Gif, 160, 104)]
    [TestCase("photo.webp", AvifSourceFormat.WebP, 320, 207)]
    [TestCase("exif6.jpg", AvifSourceFormat.Jpeg, 600, 800)]
    public void Header_reader_reports_the_stored_size_for_each_format(string file, AvifSourceFormat format, int width, int height)
    {
        ModelImageHeader.TryRead(Fixtures.Read(file), format, out var w, out var h).ShouldBeTrue();
        w.ShouldBe(width);
        h.ShouldBe(height);
    }

    [Test]
    public void Header_reader_returns_false_for_truncated_or_foreign_headers()
    {
        ModelImageHeader.TryRead(new byte[] { 0x89, 0x50 }, AvifSourceFormat.Png, out _, out _).ShouldBeFalse();
        ModelImageHeader.TryRead(Fixtures.Read("photo.jpg")[..40], AvifSourceFormat.Jpeg, out _, out _).ShouldBeFalse();
        ModelImageHeader.TryRead(Fixtures.Read("photo.png"), AvifSourceFormat.Jpeg, out _, out _).ShouldBeFalse();
    }
}
