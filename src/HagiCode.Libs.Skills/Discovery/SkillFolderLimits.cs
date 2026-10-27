namespace HagiCode.Libs.Skills.Discovery;

/// <summary>Bounds applied while discovering skills in a folder.</summary>
public static class SkillFolderLimits
{
    /// <summary>The most bytes read from the start of <c>SKILL.md</c> while looking for front matter.</summary>
    public const int MaxFrontMatterBytes = 32 * 1024;

    /// <summary>The most files one skill folder may hold before it is reported as too large.</summary>
    public const int MaxFilesPerSkill = 500;

    /// <summary>The most bytes one skill folder may hold before it is reported as too large.</summary>
    public const long MaxBytesPerSkill = 20L * 1024 * 1024;

    /// <summary>The most skills returned for one scanned folder.</summary>
    public const int MaxSkillsPerFolder = 200;

    /// <summary>The pattern a skill slug must match in full.</summary>
    public const string SlugPattern = @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";
}
