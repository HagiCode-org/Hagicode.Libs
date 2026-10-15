using NetVips;
using static NetVips.Enums;

namespace HagiCode.Libs.Imaging.Engine;

/// <summary>
/// The libvips engine (NetVips managed binding over the bundled <c>libvips</c> shared library). It is the only type that
/// touches NetVips. Every loader is explicit and uses <c>failOn: Error</c> so truncated input is rejected.
/// </summary>
internal sealed class LibvipsAvifEngine : IAvifEngine
{
    /// <inheritdoc />
    public SourceHeader ReadHeader(byte[] source, AvifSourceFormat format)
    {
        using var image = Load(source, format, Access.Random);
        var (width, height) = OrientedSize(image);
        return new SourceHeader(width, height, image.HasAlpha());
    }

    /// <inheritdoc />
    public EncodedImage Convert(byte[] source, AvifSourceFormat format, AvifConversionOptions options)
    {
        using var loaded = Load(source, format, Access.Random);
        using var oriented = loaded.Autorot();
        using var rgb = EnsureSrgb(oriented);
        using var resized = Resize(rgb, options.Resize);

        Image output;
        if (options.Alpha == AvifAlphaMode.Flatten && resized.HasAlpha())
        {
            var (r, g, b) = AvifConversionOptions.ParseHexColor(options.FlattenColor);
            output = resized.Flatten([r, g, b]);
        }
        else
        {
            output = resized.Copy();
        }

        using (output)
        {
            byte[] bytes;
            try
            {
                bytes = output.HeifsaveBuffer(
                    q: options.Quality,
                    compression: ForeignHeifCompression.Av1,
                    effort: 9 - Math.Min(options.Speed, 9),
                    subsampleMode: MapSubsample(options.ChromaSubsampling),
                    keep: options.StripMetadata ? ForeignKeep.None : ForeignKeep.All);
            }
            catch (VipsException exception)
            {
                // Loading is lazy, so a truncated source often fails here, during the save. Decide on the failure path
                // whether the source itself cannot be decoded; only then is it a corrupt source.
                if (CannotDecode(source, format))
                {
                    throw new EngineLoadException("libvips could not decode the source.", exception);
                }

                throw new EngineEncodeException("libvips failed to save AVIF.", exception);
            }

            return new EncodedImage(bytes, output.Width, output.Height, output.HasAlpha());
        }
    }

    /// <summary>
    /// Loads with the explicit loader for the format. Used by the runtime probe as well, so the probe checks the same
    /// code path the converter uses.
    /// </summary>
    internal static Image Load(byte[] bytes, AvifSourceFormat format, Access? access)
    {
        try
        {
            return format switch
            {
                AvifSourceFormat.Png => Image.PngloadBuffer(bytes, access: access, failOn: FailOn.Error),
                AvifSourceFormat.Jpeg => Image.JpegloadBuffer(bytes, access: access, failOn: FailOn.Error),
                AvifSourceFormat.WebP => Image.WebploadBuffer(bytes, access: access, failOn: FailOn.Error),
                AvifSourceFormat.Gif => Image.GifloadBuffer(bytes, access: access, failOn: FailOn.Error),
                AvifSourceFormat.Avif => Image.HeifloadBuffer(bytes, access: access, failOn: FailOn.Error),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported source format."),
            };
        }
        catch (VipsException exception)
        {
            throw new EngineLoadException("libvips could not load the source.", exception);
        }
    }

    /// <summary>Forces a full decode of the source on its own. Runs only on the failure path.</summary>
    private static bool CannotDecode(byte[] source, AvifSourceFormat format)
    {
        try
        {
            using var image = Load(source, format, Access.Sequential);
            _ = image.WriteToMemory<byte>();
            return false;
        }
        catch (VipsException)
        {
            return true;
        }
        catch (EngineLoadException)
        {
            return true;
        }
    }

    private static (int Width, int Height) OrientedSize(Image image)
    {
        var width = image.Width;
        var height = image.Height;
        if (image.Contains("orientation") && image.Get("orientation") is int orientation && orientation is >= 5 and <= 8)
        {
            (width, height) = (height, width);
        }

        return (width, height);
    }

    private static Image EnsureSrgb(Image image)
    {
        if (image.Interpretation == Interpretation.Srgb && image.Bands >= 3)
        {
            return image.Copy();
        }

        return image.Colourspace(Interpretation.Srgb);
    }

    private static Image Resize(Image image, AvifResize? resize)
    {
        if (resize is null || resize.Mode == AvifResizeMode.None)
        {
            return image.Copy();
        }

        return resize.Mode == AvifResizeMode.Fit
            ? image.ThumbnailImage(resize.Width, height: resize.Height, size: Size.Down, noRotate: true)
            : image.ThumbnailImage(resize.Width, height: resize.Height, size: Size.Both, noRotate: true, crop: Interesting.Centre);
    }

    private static ForeignSubsample MapSubsample(AvifChromaSubsampling subsampling) => subsampling switch
    {
        AvifChromaSubsampling.Yuv420 => ForeignSubsample.On,
        AvifChromaSubsampling.Yuv444 => ForeignSubsample.Off,
        _ => ForeignSubsample.Auto,
    };
}
