namespace HagiCode.Libs.Imaging.Engine;

/// <summary>Header facts of a source. Width and height are reported after EXIF orientation.</summary>
internal readonly record struct SourceHeader(int Width, int Height, bool HasAlpha);

/// <summary>One encoded AVIF image and its output dimensions.</summary>
internal readonly record struct EncodedImage(byte[] Bytes, int Width, int Height, bool HasAlpha);

/// <summary>
/// Internal seam between the managed pipeline and the native imaging library. The public API never exposes it, so the
/// engine can be replaced without changing the contract. Implementations must read the header only in
/// <see cref="ReadHeader"/> and must not return native handles.
/// </summary>
internal interface IAvifEngine
{
    /// <summary>Reads the dimensions and alpha flag through the explicit loader for <paramref name="format"/>.</summary>
    SourceHeader ReadHeader(byte[] source, AvifSourceFormat format);

    /// <summary>Decodes, orients, resizes, flattens, strips and encodes to AVIF.</summary>
    EncodedImage Convert(byte[] source, AvifSourceFormat format, AvifConversionOptions options);
}

/// <summary>A decode or header failure reported by the engine. Mapped to <c>CorruptSource</c>.</summary>
internal sealed class EngineLoadException : Exception
{
    internal EngineLoadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>An encode failure reported by the engine. Mapped to <c>EncodeFailed</c>.</summary>
internal sealed class EngineEncodeException : Exception
{
    internal EngineEncodeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
