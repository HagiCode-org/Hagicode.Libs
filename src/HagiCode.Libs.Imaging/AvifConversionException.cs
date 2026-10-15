namespace HagiCode.Libs.Imaging;

/// <summary>
/// Typed conversion failure. Callers branch on <see cref="ErrorCode"/>; the message is for humans and never contains
/// source bytes.
/// </summary>
public sealed class AvifConversionException : Exception
{
    /// <summary>Creates an exception with a stable error code.</summary>
    public AvifConversionException(AvifErrorCode errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The stable error code.</summary>
    public AvifErrorCode ErrorCode { get; }
}
