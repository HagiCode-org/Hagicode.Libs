using HagiCode.Libs.Imaging.Engine;

namespace HagiCode.Libs.Imaging.Internal;

/// <summary>Pipeline stage in which a failure happened. Used only to choose a code for failures with no specific cause.</summary>
internal enum ConversionStage
{
    /// <summary>Reading the header or decoding the source.</summary>
    Header,

    /// <summary>Converting pixels and encoding AVIF.</summary>
    Encode,
}

/// <summary>
/// The only place that translates engine and loader failures into <see cref="AvifErrorCode"/> values. Messages name the
/// detected runtime identifier where relevant and never include source bytes or file contents.
/// </summary>
internal static class ErrorMapper
{
    internal static AvifConversionException Map(Exception exception, ConversionStage stage)
    {
        if (exception is AvifConversionException typed)
        {
            return typed;
        }

        var root = Unwrap(exception);
        switch (root)
        {
            case DllNotFoundException or BadImageFormatException or EntryPointNotFoundException:
                return NativeUnavailable(root);
            case EngineLoadException:
                return Corrupt(root);
            case EngineEncodeException:
                return Encode(root);
        }

        return stage == ConversionStage.Encode ? Encode(root) : Corrupt(root);
    }

    /// <summary>Builds the native runtime error with the detected identifier and the supported list.</summary>
    internal static AvifConversionException NativeUnavailable(Exception? inner = null, string? reason = null, string? runtimeIdentifier = null)
    {
        var rid = runtimeIdentifier ?? NativePlatform.DetectRuntimeIdentifier();
        var message = $"The AVIF native runtime is unavailable for runtime identifier '{rid}'. Supported runtime identifiers: {NativePlatform.SupportedList}.";
        if (reason is not null)
        {
            message += " Reason: " + reason;
        }

        return new AvifConversionException(AvifErrorCode.NativeRuntimeUnavailable, message, inner);
    }

    private static AvifConversionException Corrupt(Exception? inner) =>
        new(AvifErrorCode.CorruptSource, "The source image could not be decoded.", inner);

    private static AvifConversionException Encode(Exception? inner) =>
        new(AvifErrorCode.EncodeFailed, "The AVIF encoder failed to produce output.", inner);

    private static Exception Unwrap(Exception exception)
    {
        var current = exception;
        while (current is TypeInitializationException { InnerException: { } inner })
        {
            current = inner;
        }

        return current;
    }
}
