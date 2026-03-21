namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Normalizes optional CLI and ACP parameter values without changing meaningful internal spaces.
/// </summary>
internal static class ArgumentValueNormalizer
{
    /// <summary>
    /// Trims leading and trailing whitespace and treats empty-after-trim values as absent.
    /// </summary>
    /// <param name="value">The optional value to normalize.</param>
    /// <returns>The trimmed value, or <see langword="null" /> when no value remains.</returns>
    internal static string? NormalizeOptionalValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
