using System.Runtime.InteropServices;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

[NonParallelizable]
public class AvifRuntimeTests
{
    [Test, NativeAvifGate]
    public void Healthy_probe_reports_encoding_and_the_five_decodable_formats()
    {
        NativeRuntime.Require();

        var status = AvifRuntime.Probe();

        status.IsAvailable.ShouldBeTrue();
        status.RuntimeIdentifier.ShouldBe(RuntimeInformation.RuntimeIdentifier);
        status.DecodableFormats.ShouldBe(new[]
        {
            AvifSourceFormat.Png,
            AvifSourceFormat.Jpeg,
            AvifSourceFormat.WebP,
            AvifSourceFormat.Gif,
            AvifSourceFormat.Avif,
        });
    }

    [Test]
    public void Probe_result_is_cached_and_safe_from_many_threads()
    {
        var first = AvifRuntime.Probe();

        Parallel.For(0, 8, _ => AvifRuntime.Probe().ShouldBeSameAs(first));
        AvifRuntime.Probe().ShouldBeSameAs(first);
    }

    [Test]
    public void Simulated_unavailable_runtime_fails_the_native_check_with_the_reason()
    {
        var error = Should.Throw<InvalidOperationException>(() =>
            NativeRuntime.Require(() => new AvifRuntimeStatus("linux-musl-x64", false, [], "libvips-42.so missing")));

        error.Message.ShouldContain("linux-musl-x64");
        error.Message.ShouldContain("libvips-42.so missing");
    }
}
