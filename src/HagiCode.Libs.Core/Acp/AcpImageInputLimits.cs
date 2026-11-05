namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// The limits for inline ACP image blocks. Libs providers check against these numbers, and core prepares images against
/// the same numbers, so the values live in one place.
/// </summary>
public static class AcpImageInputLimits
{
    /// <summary>The most images inlined into one prompt.</summary>
    public const int MaxImageCount = 10;

    /// <summary>The largest single inlined image, in bytes (5 MiB).</summary>
    public const long MaxImageBytes = 5L * 1024 * 1024;

    /// <summary>The largest total of inlined image bytes in one prompt (20 MiB).</summary>
    public const long MaxTotalBytes = 20L * 1024 * 1024;

    private static readonly string[] AllowedMediaTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    /// <summary>Whether the media type is one that ACP image blocks accept.</summary>
    public static bool IsAllowedMediaType(string? mediaType)
    {
        return mediaType is not null && AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }
}
