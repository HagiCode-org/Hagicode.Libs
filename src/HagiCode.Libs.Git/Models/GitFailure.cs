namespace HagiCode.Libs.Git;

/// <summary>
/// A typed, expected failure. Carries the captured command output so callers can show or log details without re-running git.
/// </summary>
/// <param name="Code">The stable failure code.</param>
/// <param name="Message">A human-readable summary.</param>
/// <param name="ExitCode">The git exit code, when a git command produced one.</param>
/// <param name="StandardOutput">Captured standard output of the failing command, when any.</param>
/// <param name="StandardError">Captured standard error of the failing command, when any.</param>
public sealed record GitFailure(
    GitFailureCode Code,
    string Message,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null)
{
    /// <summary>
    /// Gets standard error followed by standard output, trimmed and joined by a line break. <see langword="null" /> when both are empty.
    /// </summary>
    public string? Details
    {
        get
        {
            var parts = new[] { StandardError?.Trim(), StandardOutput?.Trim() }
                .Where(part => !string.IsNullOrWhiteSpace(part));
            var joined = string.Join(Environment.NewLine, parts);
            return joined.Length == 0 ? null : joined;
        }
    }
}
