namespace HagiCode.Libs.Imaging.Engine;

/// <summary>
/// Internal seam for the model-image preparer. A decoded raster is an opaque handle: the native image never leaves the
/// engine, and the caller disposes it when the preparation is finished.
/// </summary>
internal interface IModelImageEngine
{
    /// <summary>Decodes the first frame through the explicit loader for <paramref name="format"/>, with EXIF orientation applied.</summary>
    IDecodedRaster Decode(byte[] source, AvifSourceFormat format);

    /// <summary>
    /// Rasterizes an SVG document from its buffer. There is no base path, so relative references cannot resolve. The
    /// render scale is chosen so the longest edge is at most <paramref name="longEdgeLimit"/> pixels (never enlarged).
    /// </summary>
    IDecodedRaster DecodeSvg(byte[] source, int longEdgeLimit);
}

/// <summary>A decoded image held inside the engine. Encodes produce managed byte arrays only.</summary>
internal interface IDecodedRaster : IDisposable
{
    /// <summary>Width after orientation.</summary>
    int Width { get; }

    /// <summary>Height after orientation.</summary>
    int Height { get; }

    /// <summary>True when the image has an alpha band.</summary>
    bool HasAlpha { get; }

    /// <summary>Forces every pixel to be decoded so a corrupt or truncated source fails here instead of later.</summary>
    void ForceDecode();

    /// <summary>Encodes an opaque JPEG with its longest edge at most <paramref name="longEdge"/>. Metadata is removed.</summary>
    byte[] EncodeJpeg(int longEdge, int quality);

    /// <summary>Encodes a PNG with its longest edge at most <paramref name="longEdge"/>, keeping alpha. Metadata is removed.</summary>
    byte[] EncodePng(int longEdge);
}
