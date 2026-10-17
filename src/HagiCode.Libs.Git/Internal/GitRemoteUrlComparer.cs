namespace HagiCode.Libs.Git.Internal;

/// <summary>
/// Compares two remote URLs for "same repository" purposes. Ported from the backend's <c>NormalizeRemoteUrl</c>:
/// surrounding whitespace, trailing slashes and a trailing <c>.git</c> are ignored, and the comparison is case-insensitive.
/// It deliberately does not rewrite schemes, so <c>https://</c> and <c>git@</c> spellings still differ.
/// </summary>
internal static class GitRemoteUrlComparer
{
    public static bool Matches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }
}
