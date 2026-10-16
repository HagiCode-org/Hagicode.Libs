using System.Text;
using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Internal;
using HagiCode.Libs.Imaging.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

/// <summary>Behavior of <see cref="ModelImagePreparer"/> with the real native engine: pass-through, reduction, alpha, GIF, orientation, bad input and cancellation.</summary>
[NonParallelizable]
public class ModelImagePreparerTests
{
    private static readonly AvifRuntimeStatus Available = new("linux-x64", true, [AvifSourceFormat.Png], null);

    private static readonly ModelImagePreparer Preparer = new(maxConcurrentPreparations: 2);

    [Test, NativeAvifGate]
    public async Task Supported_formats_within_limits_pass_through_byte_identical()
    {
        foreach (var file in new[] { "photo.png", "photo.jpg", "photo.webp", "anim.gif" })
        {
            var original = Fixtures.Read(file);
            var prepared = await Preparer.PrepareAsync(Fixtures.PathOf(file));

            prepared.Outcome.ShouldBe(ModelImageOutcome.PassedThrough, file);
            prepared.Bytes.ToArray().SequenceEqual(original).ShouldBeTrue(file);
            prepared.OriginalByteLength.ShouldBe(original.Length);
            prepared.PreparedByteLength.ShouldBe(original.Length);
            prepared.IsUsable.ShouldBeTrue();
        }
    }

    [Test, NativeAvifGate]
    public async Task Media_type_follows_the_content_not_the_file_name()
    {
        using var temp = new TempFile("photo.png", Fixtures.Read("photo.jpg"));

        var prepared = await Preparer.PrepareAsync(temp.Path);

        prepared.Outcome.ShouldBe(ModelImageOutcome.PassedThrough);
        prepared.MediaType.ShouldBe("image/jpeg");
    }

    [Test, NativeAvifGate]
    public async Task Oversized_jpeg_is_reduced_to_the_byte_limit_with_its_aspect_ratio()
    {
        var source = NoiseJpeg(4000, 3000);
        ((long)source.Length).ShouldBeGreaterThan(9L * 1024 * 1024 - 1);

        var prepared = await Preparer.PrepareAsync(source);

        prepared.Outcome.ShouldBe(ModelImageOutcome.Reduced);
        prepared.MediaType.ShouldBe("image/jpeg");
        prepared.PreparedByteLength.ShouldBeLessThanOrEqualTo(5L * 1024 * 1024);
        ModelImageHeader.TryRead(prepared.Bytes.Span, AvifSourceFormat.Jpeg, out var width, out var height).ShouldBeTrue();
        Math.Abs((double)width / height - 4.0 / 3.0).ShouldBeLessThan(0.002);
    }

    [Test, NativeAvifGate]
    public async Task Oversized_alpha_png_keeps_its_transparency()
    {
        var source = NoiseRgbaPng(3000, 3000);
        ((long)source.Length).ShouldBeGreaterThan(5L * 1024 * 1024);

        var prepared = await Preparer.PrepareAsync(source);

        prepared.Outcome.ShouldBe(ModelImageOutcome.Reduced);
        prepared.MediaType.ShouldBe("image/png");
        prepared.PreparedByteLength.ShouldBeLessThanOrEqualTo(5L * 1024 * 1024);
        PngColorType(prepared.Bytes.Span).ShouldBeOneOf((byte)4, (byte)6);
    }

    [Test, NativeAvifGate]
    public async Task Animated_gif_over_the_limit_becomes_a_static_first_frame()
    {
        var limits = new ModelImageLimits { MaxBytes = 10_000 };

        var prepared = await Preparer.PrepareAsync(Fixtures.Read("anim.gif"), limits);

        prepared.Outcome.ShouldBe(ModelImageOutcome.Reduced);
        prepared.MediaType.ShouldNotBe("image/gif");
        prepared.PreparedByteLength.ShouldBeLessThanOrEqualTo(10_000);
        ModelImageHeader.TryRead(prepared.Bytes.Span, AvifSourceFormat.Jpeg, out var width, out var height).ShouldBeTrue();
        width.ShouldBe(160);
        height.ShouldBe(104);
    }

    [Test, NativeAvifGate]
    public async Task Rotated_photo_that_needs_reduction_is_upright()
    {
        // exif6.jpg is stored 600x800 with orientation 6, so it displays as 800x600 (landscape).
        var limits = new ModelImageLimits { MaxBytes = 20_000 };

        var prepared = await Preparer.PrepareAsync(Fixtures.Read("exif6.jpg"), limits);

        prepared.Outcome.ShouldBe(ModelImageOutcome.Reduced);
        ModelImageHeader.TryRead(prepared.Bytes.Span, AvifSourceFormat.Jpeg, out var width, out var height).ShouldBeTrue();
        width.ShouldBeGreaterThan(height);
        Math.Abs((double)width / height - 800.0 / 600.0).ShouldBeLessThan(0.01);
    }

    [Test, NativeAvifGate]
    public async Task Image_that_cannot_fit_the_limit_is_not_preparable()
    {
        var prepared = await Preparer.PrepareAsync(Fixtures.Read("photo.png"), new ModelImageLimits { MaxBytes = 300 });

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Bytes.IsEmpty.ShouldBeTrue();
        prepared.MediaType.ShouldBeNull();
        prepared.Reason!.ShouldContain("attempts");
    }

    [Test, NativeAvifGate]
    public async Task Header_above_the_decode_ceiling_is_refused_before_decoding()
    {
        // bomb.png declares about 1.6 billion pixels in its IHDR; it must be refused from the header.
        var prepared = await Preparer.PrepareAsync(Fixtures.Read("bomb.png"));

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Reason!.ShouldContain("decode limit");
    }

    [Test, NativeAvifGate]
    public async Task Truncated_image_is_not_preparable_and_does_not_throw()
    {
        var prepared = await Preparer.PrepareAsync(Fixtures.Read("truncated.png"));

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Reason.ShouldNotBeNull();
    }

    [Test, NativeAvifGate]
    public async Task Garbage_bytes_are_not_preparable_and_do_not_throw()
    {
        var prepared = await Preparer.PrepareAsync(Encoding.ASCII.GetBytes("this is not an image"));

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Reason!.ShouldBe("The content is not a supported image format.");
    }

    [Test, NativeAvifGate]
    public async Task Missing_file_is_not_preparable()
    {
        var prepared = await Preparer.PrepareAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png"));

        prepared.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        prepared.Reason!.ShouldBe("The file is missing.");
    }

    [Test, NativeAvifGate]
    public async Task Cancellation_before_start_is_reported_as_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => Preparer.PrepareAsync(Fixtures.Read("photo.png"), cancellationToken: cancelled.Token));
    }

    [Test]
    public async Task Unavailable_runtime_passes_compliant_images_and_refuses_the_rest()
    {
        var unavailable = new AvifRuntimeStatus("linux-arm64", false, [], "forced by the test");
        var degraded = new ModelImagePreparer(new LibvipsAvifEngine(), () => unavailable, 1);

        var compliant = await degraded.PrepareAsync(Fixtures.Read("photo.jpg"));
        compliant.Outcome.ShouldBe(ModelImageOutcome.PassedThrough);

        var oversize = await degraded.PrepareAsync(Fixtures.Read("photo.png"), new ModelImageLimits { MaxBytes = 300 });
        oversize.Outcome.ShouldBe(ModelImageOutcome.NotPreparable);
        oversize.Reason!.ShouldContain("unavailable");
    }

    [Test]
    public async Task Preparer_is_registered_by_dependency_injection()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeImaging();
        using var provider = services.BuildServiceProvider();

        var preparer = provider.GetRequiredService<IModelImagePreparer>();

        preparer.ShouldBeOfType<ModelImagePreparer>();
    }

    [Test]
    public void Preparation_result_prints_sizes_but_never_bytes()
    {
        var prepared = new PreparedImage(ModelImageOutcome.PassedThrough, "image/png", new byte[] { 1, 2, 3 }, 3, 3, null);

        prepared.ToString().ShouldNotContain("AQID");
        prepared.ToString().ShouldContain("PreparedByteLength = 3");
    }

    [Test]
    public void Limits_reject_non_positive_values()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ModelImageLimits { MaxBytes = 0 }.Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new ModelImageLimits { MaxDimension = -1 }.Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new ModelImageLimits { MaxDecodePixels = 0 }.Validate());
    }

    private static byte[] NoiseJpeg(int width, int height)
    {
        using var noise = Image.Gaussnoise(width, height, mean: 128, sigma: 70);
        using var rgb = noise.Bandjoin(noise, noise);
        return rgb.JpegsaveBuffer(q: 100);
    }

    private static byte[] NoiseRgbaPng(int width, int height)
    {
        using var noise = Image.Gaussnoise(width, height, mean: 128, sigma: 70);
        using var rgba = noise.Bandjoin(noise, noise, noise);
        return rgba.PngsaveBuffer();
    }

    private static byte PngColorType(ReadOnlySpan<byte> png) => png[25];

    private sealed class TempFile : IDisposable
    {
        public TempFile(string name, byte[] bytes)
        {
            var directory = Fixtures.NewTempDirectory();
            Path = System.IO.Path.Combine(directory, name);
            File.WriteAllBytes(Path, bytes);
        }

        public string Path { get; }

        public void Dispose()
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
