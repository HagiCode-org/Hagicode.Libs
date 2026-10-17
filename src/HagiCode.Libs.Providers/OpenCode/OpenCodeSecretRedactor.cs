using System.Text.RegularExpressions;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Removes the OpenCode server password from text that may leave the lifecycle boundary
/// (captured process output, diagnostics and exception messages).
/// </summary>
internal static partial class OpenCodeSecretRedactor
{
    internal const string Marker = "<redacted>";

    public static string? Redact(string? text, string? secret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = text;
        if (!string.IsNullOrEmpty(secret))
        {
            redacted = redacted.Replace(secret, Marker, StringComparison.Ordinal);
        }

        // Covers the startup line even when the password was not captured separately.
        return PasswordLineRegex().Replace(redacted, match => $"{match.Groups[1].Value}{Marker}");
    }

    // Only a whole startup line ("server password <value>") is redacted; prose such as "the server password for this runtime" is kept.
    [GeneratedRegex(@"^([ \t]*server password[ \t]+)\S+[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PasswordLineRegex();
}
