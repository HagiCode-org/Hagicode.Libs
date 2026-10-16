using NetVips;
using static NetVips.Enums;

namespace HagiCode.Libs.Imaging.Engine;

/// <summary>
/// The libvips engine (NetVips managed binding over the bundled <c>libvips</c> shared library). It is the only type that
/// touches NetVips. Every loader is explicit and uses <c>failOn: Error</c> so truncated input is rejected.
/// </summary>
internal sealed class LibvipsAvifEngine : IAvifEngine, IModelImageEngine
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

    /// <inheritdoc />
    public IDecodedRaster Decode(byte[] source, AvifSourceFormat format)
    {
        var loaded = Load(source, format, Access.Random);
        try
        {
            return new LibvipsDecodedRaster(loaded.Autorot());
        }
        finally
        {
            loaded.Dispose();
        }
    }

    /// <inheritdoc />
    public IDecodedRaster DecodeSvg(byte[] source, int longEdgeLimit)
    {
        // The intrinsic size comes from a header-only load (nothing is rendered yet). The render then uses a scale that
        // keeps the longest edge within the limit, so a declared size of 100000 px never allocates that raster.
        double scale;
        using (var probe = LoadSvg(source, 1.0))
        {
            var longest = Math.Max(probe.Width, probe.Height);
            scale = longest <= 0 ? 1.0 : Math.Min(1.0, (double)longEdgeLimit / longest);
        }

        // Ownership moves to the returned wrapper, which disposes the native image.
        return new LibvipsDecodedRaster(LoadSvg(source, scale));
    }

    private static Image LoadSvg(byte[] source, double scale)
    {
        try
        {
            return Image.SvgloadBuffer(source, scale: scale, failOn: FailOn.Error);
        }
        catch (VipsException exception)
        {
            throw new EngineLoadException("libvips could not rasterize the SVG.", exception);
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

/// <summary>Owns one native image for the lifetime of a preparation. Only managed bytes and sizes leave it.</summary>
internal sealed class LibvipsDecodedRaster : IDecodedRaster
{
    private Image? _raster;

    internal LibvipsDecodedRaster(Image raster)
    {
        _raster = raster;
    }

    private Image Raster => _raster ?? throw new ObjectDisposedException(nameof(LibvipsDecodedRaster));

    public int Width => Raster.Width;

    public int Height => Raster.Height;

    public bool HasAlpha => Raster.HasAlpha();

    public void ForceDecode()
    {
        // Avg reads every pixel in a streaming pass, so truncated input raises here without a full-size buffer.
        _ = Raster.Avg();
    }

    public byte[] EncodeJpeg(int longEdge, int quality)
    {
        using var srgb = ToSrgb(Raster);
        using var opaque = srgb.Bands > 3 ? srgb.ExtractBand(0, n: 3) : srgb.Copy();
        using var fitted = Fit(opaque, longEdge);
        return fitted.JpegsaveBuffer(q: quality, keep: ForeignKeep.None);
    }

    public byte[] EncodePng(int longEdge)
    {
        using var srgb = ToSrgb(Raster);
        using var fitted = Fit(srgb, longEdge);
        return fitted.PngsaveBuffer(keep: ForeignKeep.None);
    }

    public void Dispose()
    {
        _raster?.Dispose();
        _raster = null;
    }

    /// <summary>Returns a new sRGB image (alpha kept). The caller disposes it.</summary>
    private static Image ToSrgb(Image image) =>
        image.Interpretation == Interpretation.Srgb ? image.Copy() : image.Colourspace(Interpretation.Srgb);

    private static Image Fit(Image image, int longEdge)
    {
        if (longEdge >= Math.Max(image.Width, image.Height))
        {
            return image.Copy();
        }

        // A square box keeps the aspect ratio of the longest edge, and Size.Down never enlarges.
        return image.ThumbnailImage(longEdge, height: longEdge, size: Size.Down, noRotate: true);
    }
}
