using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HagiCode.Libs.Skills.Discovery;

/// <summary>
/// Finds skill folders one level below a folder. A skill is an immediate, non-hidden sub-folder that holds a
/// <c>SKILL.md</c> file. The scanner only reads; it never creates, changes or deletes anything.
/// </summary>
public sealed class SkillFolderScanner : ISkillFolderScanner
{
    private static readonly Regex SlugRegex = new(SkillFolderLimits.SlugPattern, RegexOptions.CultureInvariant);

    private static readonly EnumerationOptions StrictEnumeration = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.None,
    };

    private readonly SkillDocumentParser _parser = new();

    /// <inheritdoc />
    public SkillFolderScanResult Scan(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        var root = SkillPathResolver.MakeAbsolute(folderPath);
        var checkFailure = CheckFolder(root);
        if (checkFailure is not null)
        {
            return Failed(checkFailure.Value);
        }

        if (!SkillPathResolver.TryResolveRealPath(root, out var rootReal))
        {
            return Failed(SkillFolderScanFailure.AccessDenied);
        }

        List<Candidate> candidates;
        try
        {
            candidates = ListCandidates(root, rootReal, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failed(SkillFolderScanFailure.NotFound);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed(SkillFolderScanFailure.AccessDenied);
        }

        candidates.Sort(static (left, right) => string.CompareOrdinal(left.Slug, right.Slug));
        var truncated = candidates.Count > SkillFolderLimits.MaxSkillsPerFolder;
        var skills = new List<DiscoveredSkill>(Math.Min(candidates.Count, SkillFolderLimits.MaxSkillsPerFolder));
        foreach (var candidate in candidates.Take(SkillFolderLimits.MaxSkillsPerFolder))
        {
            skills.Add(ScanSkill(candidate, rootReal, cancellationToken));
        }

        return new SkillFolderScanResult(skills, truncated, null);
    }

    /// <inheritdoc />
    public Task<SkillFolderScanResult> ScanAsync(string folderPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(folderPath, cancellationToken), cancellationToken);

    private static SkillFolderScanFailure? CheckFolder(string root)
    {
        try
        {
            var attributes = File.GetAttributes(root);
            return (attributes & FileAttributes.Directory) == 0 ? SkillFolderScanFailure.NotADirectory : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SkillFolderScanFailure.NotFound;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SkillFolderScanFailure.AccessDenied;
        }
    }

    private static List<Candidate> ListCandidates(string root, string rootReal, CancellationToken cancellationToken)
    {
        List<Candidate> candidates = [];
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", StrictEnumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            if (name.StartsWith('.') || !Directory.Exists(entry))
            {
                continue;
            }

            // A link that leaves the scanned folder is listed so the problem is visible. It is not followed.
            if (SkillPathResolver.IsLink(entry)
                && (!SkillPathResolver.TryResolveRealPath(entry, out var realEntry) || !SkillPathResolver.IsWithin(realEntry, rootReal)))
            {
                candidates.Add(new Candidate(name, entry, EscapesRoot: true));
                continue;
            }

            if (HasSkillFile(entry))
            {
                candidates.Add(new Candidate(name, entry, EscapesRoot: false));
            }
        }

        return candidates;
    }

    private static bool HasSkillFile(string folder)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder, SkillDocumentParser.SkillFileName, StrictEnumeration))
            {
                if (Path.GetFileName(entry) == SkillDocumentParser.SkillFileName && File.Exists(entry))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A sub-folder that cannot be listed cannot be shown to hold a skill, so it is not listed.
            return false;
        }

        return false;
    }

    private DiscoveredSkill ScanSkill(Candidate candidate, string rootReal, CancellationToken cancellationToken)
    {
        List<SkillProblem> problems = [];
        var slugMatch = SlugRegex.Match(candidate.Slug);
        if (!slugMatch.Success || slugMatch.Length != candidate.Slug.Length)
        {
            problems.Add(new SkillProblem(SkillProblemCodes.InvalidSlug, "Folder name is not a valid skill name"));
        }

        if (candidate.EscapesRoot
            || !SkillPathResolver.TryResolveRealPath(candidate.Path, out var skillReal)
            || !SkillPathResolver.IsWithin(skillReal, rootReal))
        {
            problems.Add(new SkillProblem(SkillProblemCodes.UnsafeLink, "Skill folder is a link that points outside the scanned folder"));
            return new DiscoveredSkill(candidate.Slug, candidate.Path, null, null, null, problems);
        }

        var walk = new SkillWalkState(rootReal);
        try
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { skillReal };
            WalkDirectory(candidate.Path, string.Empty, walk, visited, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add(new SkillProblem(SkillProblemCodes.Unreadable, "Skill files cannot be read"));
            return new DiscoveredSkill(candidate.Slug, candidate.Path, null, null, null, problems);
        }

        if (walk.UnsafeLinkFound)
        {
            problems.Add(new SkillProblem(SkillProblemCodes.UnsafeLink, "Skill contains a link that points outside the scanned folder"));
        }

        if (walk.TooLarge)
        {
            problems.Add(new SkillProblem(SkillProblemCodes.TooLarge, "Skill has more than 500 files or more than 20 MiB"));
        }

        string? name = null;
        string? description = null;
        if (!walk.SkillFileUnsafe)
        {
            var metadata = _parser.ParseFile(Path.Combine(candidate.Path, SkillDocumentParser.SkillFileName));
            name = metadata.Name;
            description = metadata.Description;
            problems.AddRange(metadata.Problems);
        }

        var fingerprint = problems.Count == 0 ? ComputeFingerprint(walk.Files) : null;
        return new DiscoveredSkill(candidate.Slug, candidate.Path, name, description, fingerprint, problems);
    }

    private static void WalkDirectory(
        string folder,
        string relativePrefix,
        SkillWalkState walk,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", StrictEnumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (walk.Stopped)
            {
                return;
            }

            var name = Path.GetFileName(entry);
            var relative = relativePrefix + name;
            string? realEntry = null;
            if (SkillPathResolver.IsLink(entry))
            {
                if (!SkillPathResolver.TryResolveRealPath(entry, out var resolved) || !SkillPathResolver.IsWithin(resolved, walk.RootReal))
                {
                    walk.UnsafeLinkFound = true;
                    if (relative == SkillDocumentParser.SkillFileName)
                    {
                        walk.SkillFileUnsafe = true;
                    }

                    continue;
                }

                realEntry = resolved;
            }

            if (Directory.Exists(entry))
            {
                // A directory link can point back into the skill; each real directory is walked once.
                if (realEntry is not null && !visited.Add(realEntry))
                {
                    continue;
                }

                WalkDirectory(entry, relative + "/", walk, visited, cancellationToken);
                continue;
            }

            if (File.Exists(entry))
            {
                walk.AddFile(relative, new FileInfo(entry));
            }
        }
    }

    private static string ComputeFingerprint(List<SkillFileEntry> files)
    {
        files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        var builder = new StringBuilder();
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(file.RelativePath)
                .Append('|')
                .Append(file.Size.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(file.LastWriteTicks.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static SkillFolderScanResult Failed(SkillFolderScanFailure failure) =>
        new([], Truncated: false, failure);

    private sealed record Candidate(string Slug, string Path, bool EscapesRoot);

    private sealed record SkillFileEntry(string RelativePath, long Size, long LastWriteTicks);

    private sealed class SkillWalkState(string rootReal)
    {
        public string RootReal { get; } = rootReal;

        public List<SkillFileEntry> Files { get; } = [];

        public long TotalBytes { get; private set; }

        public bool Stopped { get; private set; }

        public bool TooLarge { get; private set; }

        public bool UnsafeLinkFound { get; set; }

        public bool SkillFileUnsafe { get; set; }

        public void AddFile(string relativePath, FileInfo file)
        {
            if (Files.Count >= SkillFolderLimits.MaxFilesPerSkill)
            {
                Stop(tooLarge: true);
                return;
            }

            var size = file.Length;
            if (TotalBytes + size > SkillFolderLimits.MaxBytesPerSkill)
            {
                Stop(tooLarge: true);
                return;
            }

            TotalBytes += size;
            Files.Add(new SkillFileEntry(relativePath, size, file.LastWriteTimeUtc.Ticks));
        }

        private void Stop(bool tooLarge)
        {
            TooLarge = tooLarge;
            Stopped = true;
        }
    }
}
