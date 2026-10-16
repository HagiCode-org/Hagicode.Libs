using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Internal;

namespace HagiCode.Libs.Imaging;

/// <summary>
/// The default <see cref="IModelImagePreparer"/>. Supported images within the limits pass through unchanged. An SVG is
/// rasterized to PNG from its buffer. An image over a limit is reduced in a bounded number of attempts. When the
/// native runtime is unavailable only pass-through can succeed.
/// </summary>
public sealed class ModelImagePreparer : IModelImagePreparer
{
    /// <summary>The longest edge of a rasterized SVG, in pixels. Keeps the render bounded whatever the declared size.</summary>
    internal const int SvgLongEdge = 2048;

    /// <summary>The number of encode attempts when reducing an image. Each attempt shrinks or lowers quality.</summary>
    internal const int MaxReductionAttempts = 6;

    private static readonly int[] JpegQualitySteps = [85, 75, 65, 55];

    private readonly IModelImageEngine _engine;
    private readonly Func<AvifRuntimeStatus> _runtimeProbe;
    private readonly ConversionGate _gate;

    /// <summary>Creates a preparer with the libvips engine. Concurrency is half the processor count, at least one.</summary>
    public ModelImagePreparer()
        : this(Math.Max(1, Environment.ProcessorCount / 2))
    {
    }

    /// <summary>Creates a preparer with the libvips engine and the given number of concurrent preparations.</summary>
    public ModelImagePreparer(int maxConcurrentPreparations)
        : this(new LibvipsAvifEngine(), AvifRuntime.Probe, maxConcurrentPreparations)
    {
    }

    internal ModelImagePreparer(IModelImageEngine engine, Func<AvifRuntimeStatus> runtimeProbe, int maxConcurrentPreparations)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _runtimeProbe = runtimeProbe ?? throw new ArgumentNullException(nameof(runtimeProbe));
        _gate = new ConversionGate(maxConcurrentPreparations);
    }

    /// <inheritdoc />
    public async Task<PreparedImage> PrepareAsync(string path, ModelImageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var effective = Effective(limits);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return NotPreparable(0, "The file is missing.");
            }

            if (info.Length > effective.MaxSourceBytes)
            {
                return NotPreparable(info.Length, "The file is larger than the reading limit.");
            }

            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return NotPreparable(0, $"The file could not be read ({exception.GetType().Name}).");
        }

        return await PrepareAsync(bytes, effective, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PreparedImage> PrepareAsync(ReadOnlyMemory<byte> source, ModelImageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var effective = Effective(limits);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Length > effective.MaxSourceBytes)
        {
            return NotPreparable(source.Length, "The image is larger than the reading limit.");
        }

        var bytes = source.ToArray();
        using var lease = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await Task.Run(() => Prepare(bytes, effective, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static ModelImageLimits Effective(ModelImageLimits? limits)
    {
        var effective = limits ?? ModelImageLimits.Default;
        effective.Validate();
        return effective;
    }

    private PreparedImage Prepare(byte[] bytes, ModelImageLimits limits, CancellationToken cancellationToken)
    {
        try
        {
            var raster = SourceSniffer.Detect(bytes);
            if (raster is AvifSourceFormat.Avif)
            {
                return NotPreparable(bytes.Length, "AVIF is not a format the model accepts.");
            }

            if (raster is { } format)
            {
                return PrepareRaster(bytes, format, limits, cancellationToken);
            }

            if (SourceSniffer.IsSvg(bytes))
            {
                return PrepareSvg(bytes, limits, cancellationToken);
            }

            return NotPreparable(bytes.Length, "The content is not a supported image format.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never throw for bad input. The exception type is enough for diagnosis; the message may echo content.
            return NotPreparable(bytes.Length, $"The image could not be prepared ({exception.GetType().Name}).");
        }
    }

    private PreparedImage PrepareRaster(byte[] bytes, AvifSourceFormat format, ModelImageLimits limits, CancellationToken cancellationToken)
    {
        var mediaType = MediaTypeOf(format);
        var fitsBytes = bytes.Length <= limits.MaxBytes;
        var hasHeader = ModelImageHeader.TryRead(bytes, format, out var width, out var height);

        // Header-first: a declared size above the decode ceiling is refused before any pixel is decoded.
        if (hasHeader && (long)width * height > limits.MaxDecodePixels)
        {
            return NotPreparable(bytes.Length, "The image has more pixels than the decode limit.");
        }

        var runtime = _runtimeProbe();
        var fitsDimensions = hasHeader && width <= limits.MaxDimension && height <= limits.MaxDimension;
        if (fitsBytes && fitsDimensions)
        {
            if (!runtime.IsAvailable)
            {
                // Pass-through does not need the native runtime. In this mode the body is not decode-checked.
                return Passed(bytes, mediaType);
            }

            using var verified = DecodeOrFail(bytes, format, out var failure);
            if (verified is null)
            {
                return NotPreparable(bytes.Length, failure!);
            }

            cancellationToken.ThrowIfCancellationRequested();
            verified.ForceDecode();
            return Passed(bytes, mediaType);
        }

        if (!runtime.IsAvailable)
        {
            return NotPreparable(bytes.Length, UnavailableReason(runtime));
        }

        using var decoded = DecodeOrFail(bytes, format, out var decodeFailure);
        if (decoded is null)
        {
            return NotPreparable(bytes.Length, decodeFailure!);
        }

        if ((long)decoded.Width * decoded.Height > limits.MaxDecodePixels)
        {
            return NotPreparable(bytes.Length, "The image has more pixels than the decode limit.");
        }

        if (fitsBytes && decoded.Width <= limits.MaxDimension && decoded.Height <= limits.MaxDimension)
        {
            // The header could not be read, but the decoded size fits: pass the original bytes through after a full decode.
            decoded.ForceDecode();
            return Passed(bytes, mediaType);
        }

        return Reduce(decoded, bytes.Length, limits, ModelImageOutcome.Reduced, cancellationToken);
    }

    private PreparedImage PrepareSvg(byte[] bytes, ModelImageLimits limits, CancellationToken cancellationToken)
    {
        if (ContainsEntityDeclaration(bytes))
        {
            return NotPreparable(bytes.Length, "SVG entity declarations are not accepted.");
        }

        var runtime = _runtimeProbe();
        if (!runtime.IsAvailable)
        {
            return NotPreparable(bytes.Length, UnavailableReason(runtime));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var longEdge = Math.Min(SvgLongEdge, limits.MaxDimension);
        IDecodedRaster rasterized;
        try
        {
            rasterized = _engine.DecodeSvg(bytes, longEdge);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return NotPreparable(bytes.Length, $"The SVG could not be rasterized ({exception.GetType().Name}).");
        }

        using (rasterized)
        {
            if ((long)rasterized.Width * rasterized.Height > limits.MaxDecodePixels)
            {
                return NotPreparable(bytes.Length, "The SVG has more pixels than the decode limit.");
            }

            var png = rasterized.EncodePng(Math.Max(rasterized.Width, rasterized.Height));
            if (png.Length <= limits.MaxBytes && rasterized.Width <= limits.MaxDimension && rasterized.Height <= limits.MaxDimension)
            {
                return new PreparedImage(ModelImageOutcome.Rasterized, "image/png", png, bytes.Length, png.Length, "SVG rasterized to PNG.");
            }

            return Reduce(rasterized, bytes.Length, limits, ModelImageOutcome.Rasterized, cancellationToken);
        }
    }

    /// <summary>
    /// Downscales and re-encodes a decoded image for at most <see cref="MaxReductionAttempts"/> attempts. Opaque images
    /// use JPEG with stepping quality; images with alpha use PNG. Each failed attempt shrinks the longest edge in
    /// proportion to the excess size, so the aspect ratio is kept.
    /// </summary>
    private PreparedImage Reduce(IDecodedRaster raster, long originalLength, ModelImageLimits limits, ModelImageOutcome successOutcome, CancellationToken cancellationToken)
    {
        var edge = Math.Min(Math.Max(raster.Width, raster.Height), limits.MaxDimension);
        var usePng = raster.HasAlpha;
        for (var attempt = 0; attempt < MaxReductionAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] encoded;
            string mediaType;
            if (usePng)
            {
                encoded = raster.EncodePng(edge);
                mediaType = "image/png";
            }
            else
            {
                var quality = JpegQualitySteps[Math.Min(attempt, JpegQualitySteps.Length - 1)];
                encoded = raster.EncodeJpeg(edge, quality);
                mediaType = "image/jpeg";
            }

            if (encoded.Length <= limits.MaxBytes)
            {
                var reason = successOutcome == ModelImageOutcome.Rasterized
                    ? "SVG rasterized and reduced to fit the limit."
                    : "Reduced to fit the limit.";
                return new PreparedImage(successOutcome, mediaType, encoded, originalLength, encoded.Length, reason);
            }

            var shrink = Math.Clamp(Math.Sqrt((double)limits.MaxBytes / encoded.Length) * 0.9, 0.25, 0.9);
            var next = (int)(edge * shrink);
            if (next < 1 || next == edge && attempt >= JpegQualitySteps.Length - 1)
            {
                break;
            }

            edge = Math.Max(1, next);
        }

        return NotPreparable(originalLength, $"The image does not fit the size limit after {MaxReductionAttempts} attempts.");
    }

    private IDecodedRaster? DecodeOrFail(byte[] bytes, AvifSourceFormat format, out string? failure)
    {
        try
        {
            failure = null;
            return _engine.Decode(bytes, format);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception text can echo file content, so only the fixed phrase leaves this method.
            failure = "The image could not be decoded.";
            return null;
        }
    }

    private static bool ContainsEntityDeclaration(byte[] bytes) =>
        bytes.AsSpan().IndexOf("<!ENTITY"u8) >= 0;

    private static string UnavailableReason(AvifRuntimeStatus runtime) =>
        $"The native imaging runtime is unavailable: {runtime.UnavailableReason ?? "unknown reason"}.";

    private static string MediaTypeOf(AvifSourceFormat format) => format switch
    {
        AvifSourceFormat.Png => "image/png",
        AvifSourceFormat.Jpeg => "image/jpeg",
        AvifSourceFormat.Gif => "image/gif",
        AvifSourceFormat.WebP => "image/webp",
        _ => "application/octet-stream",
    };

    private static PreparedImage Passed(byte[] bytes, string mediaType) =>
        new(ModelImageOutcome.PassedThrough, mediaType, bytes, bytes.Length, bytes.Length, null);

    private static PreparedImage NotPreparable(long originalLength, string reason) =>
        new(ModelImageOutcome.NotPreparable, null, ReadOnlyMemory<byte>.Empty, originalLength, 0, reason);
}
