using System.Diagnostics;
using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Internal;

namespace HagiCode.Libs.Imaging;

/// <summary>
/// The in-process AVIF converter. Register it through <c>AddHagiCodeImaging</c> or construct it directly. One instance
/// is safe to share across threads and is intended to be a singleton.
/// </summary>
public sealed class AvifConverter : IAvifConverter, IDisposable
{
    private readonly AvifConverterOptions _limits;
    private readonly IAvifEngine _engine;
    private readonly Func<AvifRuntimeStatus> _runtimeProbe;
    private readonly ConversionGate _gate;
    private int _disposed;

    /// <summary>Creates a converter with the libvips engine and the given limits.</summary>
    public AvifConverter(AvifConverterOptions options)
        : this(Configure(options), new LibvipsAvifEngine(), AvifRuntime.Probe)
    {
    }

    /// <summary>Creates a converter with the libvips engine and default limits.</summary>
    public AvifConverter()
        : this(new AvifConverterOptions())
    {
    }

    internal AvifConverter(AvifConverterOptions options, IAvifEngine engine, Func<AvifRuntimeStatus> runtimeProbe)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _limits = options.Snapshot();
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _runtimeProbe = runtimeProbe ?? throw new ArgumentNullException(nameof(runtimeProbe));
        _gate = new ConversionGate(_limits.MaxConcurrentConversions);
    }

    /// <inheritdoc />
    public async Task<AvifConversionResult> ConvertAsync(
        ReadOnlyMemory<byte> source,
        AvifConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (result, output) = await RunAsync(
            _ => Task.FromResult(CopyBuffer(source)),
            options,
            cancellationToken).ConfigureAwait(false);
        return result with { Data = output };
    }

    /// <inheritdoc />
    public async Task<AvifConversionResult> ConvertAsync(
        Stream source,
        Stream destination,
        AvifConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var (result, output) = await RunAsync(
            token => BoundedSourceReader.ReadAsync(source, _limits.MaxInputBytes, token),
            options,
            cancellationToken).ConfigureAwait(false);

        // Written only after a successful encode, so a failed conversion leaves the destination with zero new bytes.
        await destination.WriteAsync(output, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<AvifConversionResult> ConvertFileAsync(
        string sourcePath,
        string destinationPath,
        AvifConversionOptions? options = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ThrowIfDisposed();

        (options ?? new AvifConversionOptions()).Validate();
        AtomicFileWriter.EnsureNotSameFile(sourcePath, destinationPath);
        AtomicFileWriter.ValidateDestination(destinationPath, overwrite);

        var (result, output) = await RunAsync(
            token => ReadFileAsync(sourcePath, token),
            options,
            cancellationToken).ConfigureAwait(false);

        AtomicFileWriter.Write(destinationPath, output, overwrite);
        return result;
    }

    /// <summary>Releases the converter. Safe to call more than once. Conversions started before this call finish.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private static AvifConverterOptions Configure(AvifConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.ConfigureProcessWideEngineSettings)
        {
            AvifRuntime.ConfigureProcessWideSettings(options.MaxConcurrentConversions);
        }

        return options;
    }

    private async Task<(AvifConversionResult Result, byte[] Output)> RunAsync(
        Func<CancellationToken, Task<byte[]>> readSource,
        AvifConversionOptions? options,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var effective = options ?? new AvifConversionOptions();
        effective.Validate();

        var runtime = _runtimeProbe();
        if (!runtime.IsAvailable)
        {
            throw ErrorMapper.NativeUnavailable(reason: runtime.UnavailableReason, runtimeIdentifier: runtime.RuntimeIdentifier);
        }

        var started = Stopwatch.GetTimestamp();
        using var lease = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var source = await readSource(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var format = SourceSniffer.Detect(source)
            ?? throw new AvifConversionException(AvifErrorCode.UnsupportedSourceFormat, "The source is not PNG, JPEG, WebP, GIF or AVIF.");

        SourceHeader header;
        try
        {
            header = await Task.Run(() => _engine.ReadHeader(source, format)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ErrorMapper.Map(exception, ConversionStage.Header);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if ((long)header.Width * header.Height > _limits.MaxPixels)
        {
            throw new AvifConversionException(
                AvifErrorCode.DimensionsTooLarge,
                $"The source declares {header.Width}x{header.Height} pixels, above the {_limits.MaxPixels}-pixel limit.");
        }

        EncodedImage encoded;
        try
        {
            encoded = await Task.Run(() => _engine.Convert(source, format, effective)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ErrorMapper.Map(exception, ConversionStage.Encode);
        }

        // A native encode call cannot be interrupted, so cancellation is honored here and the output is discarded.
        cancellationToken.ThrowIfCancellationRequested();

        var result = new AvifConversionResult
        {
            SourceFormat = format,
            SourceWidth = header.Width,
            SourceHeight = header.Height,
            OutputWidth = encoded.Width,
            OutputHeight = encoded.Height,
            OutputByteLength = encoded.Bytes.LongLength,
            HasAlpha = encoded.HasAlpha,
            Elapsed = Stopwatch.GetElapsedTime(started),
        };
        return (result, encoded.Bytes);
    }

    private byte[] CopyBuffer(ReadOnlyMemory<byte> source)
    {
        BoundedSourceReader.EnsureBufferWithinLimit(source.Length, _limits.MaxInputBytes);
        return source.ToArray();
    }

    private async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        BoundedSourceReader.EnsureBufferWithinLimit(new FileInfo(path).Length, _limits.MaxInputBytes);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BoundedSourceReader.ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await BoundedSourceReader.ReadAsync(stream, _limits.MaxInputBytes, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AvifConverter));
        }
    }
}
