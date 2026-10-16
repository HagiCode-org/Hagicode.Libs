using System.Text;
using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Internal;
using HagiCode.Libs.Imaging.Tests.Support;
using NetVips;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

/// <summary>
/// SVG rasterization checks. libvips refuses <c>svgload</c> while untrusted-operation blocking is on, and the converter
/// turns blocking on for the process. These tests therefore make the state explicit: the rasterization tests set
/// blocking off and restore it, and the 1.4a probe documents the refusal while it is on.
/// </summary>
[NonParallelizable]
public class ModelImageSvgTests
{
    private static readonly ModelImagePreparer Preparer = new(maxConcurrentPreparations: 1);

    [Test]
    public void Libvips_refuses_the_svg_loader_while_untrusted_blocking_is_on()
    {
        // Task 1.4a probe. Result recorded in the Imaging README: the SVG loader is treated as untrusted.
        var engine = new LibvipsAvifEngine();
        WithBlocking(true, () =>
        {
            Should.Throw<EngineLoadException>(() => engine.DecodeSvg(Svg("<svg width='10' height='10'/>"), 64));
        });
    }

    [Test]
    public async Task Svg_is_not_preparable_while_untrusted_blocking_is_on()
    {
        var prepared = await WithBlockingAsync(true, () => Preparer.PrepareAsync(Svg("<svg width='10' height='10'/>")));

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Reason!.ShouldContain("could not be rasterized");
    }

    [Test, NativeAvifGate]
    public async Task Simple_svg_is_rasterized_to_png()
    {
        var source = Svg("<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50' viewBox='0 0 100 50'><rect width='100' height='50' fill='red'/></svg>");

        var prepared = await WithBlockingAsync(false, () => Preparer.PrepareAsync(source));

        prepared.Outcome.ShouldBe(ModelImageOutcome.Rasterized);
        prepared.MediaType.ShouldBe("image/png");
        ModelImageHeader.TryRead(prepared.Bytes.Span, AvifSourceFormat.Png, out var width, out var height).ShouldBeTrue();
        width.ShouldBe(100);
        height.ShouldBe(50);
    }

    [Test, NativeAvifGate]
    public async Task Svg_referencing_local_and_network_resources_does_not_load_them()
    {
        // The canary is solid red. If the local reference were read, the centre of the render would turn red.
        using var red = Image.Black(40, 40, bands: 3) + new double[] { 255, 0, 0 };
        var canary = Path.Combine(Path.GetTempPath(), "hagi-svg-canary-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(canary, red.PngsaveBuffer());
        try
        {
            var source = Svg(
                "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='100' height='100' viewBox='0 0 100 100'>" +
                "<rect width='100' height='100' fill='white'/>" +
                $"<image href='{canary}' x='0' y='0' width='100' height='100'/>" +
                $"<image xlink:href='file://{canary}' x='0' y='0' width='100' height='100'/>" +
                "<image href='http://127.0.0.1:9/never.png' width='100' height='100'/>" +
                "</svg>");

            var prepared = await WithBlockingAsync(false, () => Preparer.PrepareAsync(source));

            prepared.Outcome.ShouldBe(ModelImageOutcome.Rasterized);
            var centre = Decode(prepared.Bytes.ToArray()).Getpoint(50, 50).Select(v => (int)v).ToArray();
            centre[0].ShouldBe(255);
            centre[1].ShouldBe(255);
            centre[2].ShouldBe(255);
        }
        finally
        {
            File.Delete(canary);
        }
    }

    [Test, NativeAvifGate]
    public async Task Svg_with_enormous_declared_size_is_rendered_within_the_limit()
    {
        var source = Svg("<svg xmlns='http://www.w3.org/2000/svg' width='100000' height='50000'><rect width='10' height='10' fill='blue'/></svg>");

        var prepared = await WithBlockingAsync(false, () => Preparer.PrepareAsync(source));

        prepared.Outcome.ShouldBe(ModelImageOutcome.Rasterized);
        ModelImageHeader.TryRead(prepared.Bytes.Span, AvifSourceFormat.Png, out var width, out var height).ShouldBeTrue();
        Math.Max(width, height).ShouldBeLessThanOrEqualTo(ModelImagePreparer.SvgLongEdge);
        Math.Max(width, height).ShouldBe(ModelImagePreparer.SvgLongEdge);
    }

    [Test, NativeAvifGate]
    public async Task Malformed_svg_is_not_preparable()
    {
        var prepared = await WithBlockingAsync(false, () => Preparer.PrepareAsync(Svg("<svg xmlns='http://www.w3.org/2000/svg' width='10'><rect")));

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Reason!.ShouldContain("could not be rasterized");
    }

    [Test, NativeAvifGate]
    public async Task Svg_with_entity_declarations_is_refused_before_rendering()
    {
        var source = Svg("<?xml version='1.0'?><!DOCTYPE svg [<!ENTITY a 'aaaa'>]><svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>");

        var prepared = await WithBlockingAsync(false, () => Preparer.PrepareAsync(source));

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Reason!.ShouldContain("entity");
    }

    private static byte[] Svg(string text) => Encoding.UTF8.GetBytes(text);

    private static Image Decode(byte[] png) => Image.PngloadBuffer(png);

    private static void WithBlocking(bool blocking, Action action)
    {
        var previous = true;
        NetVips.NetVips.BlockUntrusted = blocking;
        try
        {
            action();
        }
        finally
        {
            NetVips.NetVips.BlockUntrusted = previous;
        }
    }

    private static async Task<T> WithBlockingAsync<T>(bool blocking, Func<Task<T>> action)
    {
        NetVips.NetVips.BlockUntrusted = blocking;
        try
        {
            return await action();
        }
        finally
        {
            // The converter sets blocking on for the process; restore that default after each SVG test.
            NetVips.NetVips.BlockUntrusted = true;
        }
    }
}
