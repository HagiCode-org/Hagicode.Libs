namespace HagiCode.Libs.Skills.Discovery;

/// <summary>Why a folder scan failed. A failed scan returns no skills.</summary>
public enum SkillFolderScanFailure
{
    /// <summary>The scanned path does not exist.</summary>
    NotFound,

    /// <summary>The scanned path exists but is a regular file.</summary>
    NotADirectory,

    /// <summary>The folder cannot be listed because of permissions or another I/O error.</summary>
    AccessDenied,
}

/// <summary>A reason a skill is not loadable. Messages are short English text for diagnostics.</summary>
/// <param name="Code">A stable code from <see cref="SkillProblemCodes" />.</param>
/// <param name="Message">A short description that may name the field involved.</param>
public sealed record SkillProblem(string Code, string Message);

/// <summary>Stable problem codes reported by the skill folder scanner.</summary>
public static class SkillProblemCodes
{
    public const string MissingName = "missing-name";
    public const string MissingDescription = "missing-description";
    public const string UnreadableMetadata = "unreadable-metadata";
    public const string OversizedMetadata = "oversized-metadata";
    public const string InvalidSlug = "invalid-slug";
    public const string UnsafeLink = "unsafe-link";
    public const string TooLarge = "too-large";
    public const string Unreadable = "unreadable";
}

/// <summary>One skill folder found by a scan.</summary>
/// <param name="Slug">The folder name, which is the skill slug.</param>
/// <param name="Path">The absolute path of the skill folder.</param>
/// <param name="Name">The front matter <c>name</c>, or null when it is missing or unreadable.</param>
/// <param name="Description">The front matter <c>description</c>, or null when it is missing or unreadable.</param>
/// <param name="Fingerprint">A lowercase SHA-256 hex digest. Non-null only for loadable skills.</param>
/// <param name="Problems">The reasons the skill is not loadable. Empty when there are none.</param>
public sealed record DiscoveredSkill(
    string Slug,
    string Path,
    string? Name,
    string? Description,
    string? Fingerprint,
    IReadOnlyList<SkillProblem> Problems)
{
    /// <summary>True when the skill has no problems and has a fingerprint.</summary>
    public bool IsLoadable => Problems.Count == 0 && Fingerprint is not null;
}

/// <summary>The outcome of scanning one folder for skills.</summary>
/// <param name="Skills">The skills in slug order, at most 200. Empty when the scan failed.</param>
/// <param name="Truncated">True when the folder held more skills than were returned.</param>
/// <param name="Failure">The reason the folder could not be scanned, or null on success.</param>
public sealed record SkillFolderScanResult(
    IReadOnlyList<DiscoveredSkill> Skills,
    bool Truncated,
    SkillFolderScanFailure? Failure)
{
    /// <summary>True when the folder was scanned.</summary>
    public bool Succeeded => Failure is null;
}
