namespace HagiCode.Libs.Git.Internal;

/// <summary>The parsed output of <c>git status --porcelain=2 -z --branch</c>.</summary>
/// <param name="Branch">The branch from <c># branch.head</c>, or <see langword="null" /> when detached.</param>
/// <param name="Entries">Every reported path, in git's order.</param>
internal sealed record PorcelainStatus(string? Branch, IReadOnlyList<GitStatusEntry> Entries);

/// <summary>
/// Parses porcelain v2 output with NUL separators. Only machine formats are read, so the result does not depend on the
/// user's locale or on how git prints for humans. Renames consume the following segment as the original path.
/// </summary>
internal static class GitPorcelainV2Parser
{
    private const char Nul = '\0';

    public static PorcelainStatus Parse(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return new PorcelainStatus(null, []);
        }

        string? branch = null;
        var entries = new List<GitStatusEntry>();
        var segments = output.Split(Nul, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];

            if (segment.StartsWith("# ", StringComparison.Ordinal))
            {
                if (segment.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    var value = segment["# branch.head ".Length..].Trim();
                    if (value.Length > 0 && !string.Equals(value, "(detached)", StringComparison.OrdinalIgnoreCase))
                    {
                        branch = value;
                    }
                }

                continue;
            }

            switch (segment[0])
            {
                case '?':
                    if (segment.Length > 2)
                    {
                        entries.Add(new GitStatusEntry(segment[2..], null, GitChangeKind.None, GitChangeKind.Untracked));
                    }

                    break;

                case '1':
                    AddTrackedEntry(segment, prefixFieldCount: 8, entries, originalPath: null);
                    break;

                case '2':
                {
                    // With -z the original path of a rename or copy is the next NUL-separated segment.
                    string? originalPath = null;
                    if (index + 1 < segments.Length)
                    {
                        originalPath = segments[index + 1];
                        index++;
                    }

                    AddTrackedEntry(segment, prefixFieldCount: 9, entries, originalPath);
                    break;
                }

                case 'u':
                    AddUnmergedEntry(segment, entries);
                    break;
            }
        }

        return new PorcelainStatus(branch, entries);
    }

    /// <summary>
    /// Paths for <c>stage-all</c>: untracked entries, unmerged entries, and tracked entries whose index side is unchanged.
    /// This mirrors the rule the backend has always used, so the count it reports does not move.
    /// </summary>
    public static IReadOnlyList<string> StageAllTargets(IEnumerable<GitStatusEntry> entries)
    {
        return entries
            .Where(entry => entry.WorkingTreeChange == GitChangeKind.Untracked
                || entry.IndexChange == GitChangeKind.Unmerged
                || (entry.IndexChange == GitChangeKind.None && entry.WorkingTreeChange != GitChangeKind.None))
            .Select(entry => entry.Path)
            .ToList();
    }

    /// <summary>
    /// Paths for <c>unstage-all</c>: tracked entries with a staged change. Renames use the new path.
    /// </summary>
    public static IReadOnlyList<string> UnstageAllTargets(IEnumerable<GitStatusEntry> entries)
    {
        return entries
            .Where(entry => entry.IndexChange is not GitChangeKind.None and not GitChangeKind.Unmerged and not GitChangeKind.Untracked)
            .Select(entry => entry.Path)
            .ToList();
    }

    private static void AddTrackedEntry(string segment, int prefixFieldCount, List<GitStatusEntry> entries, string? originalPath)
    {
        if (segment.Length < 4)
        {
            return;
        }

        var path = ExtractPath(segment, prefixFieldCount);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var indexChange = FromCode(segment[2]);
        var workingTreeChange = FromCode(segment[3]);
        if (indexChange == GitChangeKind.None && workingTreeChange == GitChangeKind.None)
        {
            return;
        }

        entries.Add(new GitStatusEntry(path, originalPath, indexChange, workingTreeChange));
    }

    private static void AddUnmergedEntry(string segment, List<GitStatusEntry> entries)
    {
        if (segment.Length < 4)
        {
            return;
        }

        var path = ExtractPath(segment, prefixFieldCount: 10);
        if (!string.IsNullOrEmpty(path))
        {
            entries.Add(new GitStatusEntry(path, null, GitChangeKind.Unmerged, GitChangeKind.Unmerged));
        }
    }

    /// <summary>Returns the path that follows the given number of space-separated prefix fields. Paths may contain spaces.</summary>
    private static string? ExtractPath(string segment, int prefixFieldCount)
    {
        var position = 0;
        for (var field = 0; field < prefixFieldCount; field++)
        {
            position = segment.IndexOf(' ', position);
            if (position < 0)
            {
                return null;
            }

            position++;
        }

        return segment[position..];
    }

    private static GitChangeKind FromCode(char code) => code switch
    {
        'A' => GitChangeKind.Added,
        'M' => GitChangeKind.Modified,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Unmerged,
        _ => GitChangeKind.None
    };
}
