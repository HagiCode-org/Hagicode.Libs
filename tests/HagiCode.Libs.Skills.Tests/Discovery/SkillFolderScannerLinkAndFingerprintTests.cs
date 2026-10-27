using System.Text.RegularExpressions;
using HagiCode.Libs.Skills.Discovery;

namespace HagiCode.Libs.Skills.Tests;

public sealed class SkillFolderScannerLinkAndFingerprintTests
{
    private static readonly SkillFolderScanner Scanner = new();
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    [Test]
    public void Skill_folder_link_that_leaves_the_scanned_folder_is_listed_as_unsafe()
    {
        using var folder = new TempSkillFolder();
        using var outside = new TempSkillFolder();
        outside.WriteFile("SKILL.md", TempSkillFolder.ValidFrontMatter);
        CreateLink(folder.Combine("escape"), outside.Root);

        var result = Scanner.Scan(folder.Root);

        var skill = result.Skills.Single();
        skill.Slug.ShouldBe("escape");
        skill.IsLoadable.ShouldBeFalse();
        skill.Fingerprint.ShouldBeNull();
        skill.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.UnsafeLink]);
        skill.Name.ShouldBeNull();
    }

    [Test]
    public void File_link_inside_a_skill_that_points_outside_is_unsafe_and_the_skill_is_not_loadable()
    {
        using var folder = new TempSkillFolder();
        using var outside = new TempSkillFolder();
        var secret = outside.WriteFile("secret.md", "outside content");
        folder.WriteSkill("linked");
        CreateLink(folder.Combine("linked", "secret.md"), secret);

        var skill = Scanner.Scan(folder.Root).Skills.Single();

        skill.IsLoadable.ShouldBeFalse();
        skill.Fingerprint.ShouldBeNull();
        skill.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.UnsafeLink]);
        skill.Name.ShouldBe("demo");
    }

    [Test]
    public void SKILL_md_that_links_outside_is_not_read()
    {
        using var folder = new TempSkillFolder();
        using var outside = new TempSkillFolder();
        var outsideSkill = outside.WriteFile("external.md", TempSkillFolder.ValidFrontMatter);
        folder.WriteSkill("skill-link", "placeholder");
        File.Delete(folder.Combine("skill-link", "SKILL.md"));
        CreateLink(folder.Combine("skill-link", "SKILL.md"), outsideSkill);

        var skill = Scanner.Scan(folder.Root).Skills.Single();

        skill.IsLoadable.ShouldBeFalse();
        skill.Name.ShouldBeNull();
        skill.Description.ShouldBeNull();
        skill.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.UnsafeLink]);
    }

    [Test]
    public void Links_that_stay_inside_the_scanned_folder_are_accepted()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("alpha");
        folder.WriteFile("alpha/real.md", "shared text");
        CreateLink(folder.Combine("alpha", "linked.md"), folder.Combine("alpha", "real.md"));
        CreateLink(folder.Combine("alias"), folder.Combine("alpha"));

        var result = Scanner.Scan(folder.Root);

        var alpha = result.Skills.Single(static skill => skill.Slug == "alpha");
        var alias = result.Skills.Single(static skill => skill.Slug == "alias");
        alpha.Problems.ShouldBeEmpty();
        alias.Problems.ShouldBeEmpty();
        alpha.IsLoadable.ShouldBeTrue();
        alias.IsLoadable.ShouldBeTrue();
        alias.Fingerprint.ShouldBe(alpha.Fingerprint);
    }

    [Test]
    public void Fingerprint_is_lowercase_sha256_hex_and_unchanged_when_the_folder_is_unchanged()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("stable");
        folder.WriteFile("stable/guide.md", "guide");

        var first = Scanner.Scan(folder.Root).Skills.Single().Fingerprint;
        var second = Scanner.Scan(folder.Root).Skills.Single().Fingerprint;

        first.ShouldNotBeNull();
        Sha256Hex.IsMatch(first).ShouldBeTrue();
        second.ShouldBe(first);
    }

    [Test]
    public void Fingerprint_changes_when_a_file_is_edited()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("editable");
        var before = Scanner.Scan(folder.Root).Skills.Single().Fingerprint;

        folder.WriteFile("editable/SKILL.md", TempSkillFolder.ValidFrontMatter + "\nmore text than before\n");

        Scanner.Scan(folder.Root).Skills.Single().Fingerprint.ShouldNotBe(before);
    }

    [Test]
    public void Fingerprint_changes_when_a_file_is_added()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("growing");
        var before = Scanner.Scan(folder.Root).Skills.Single().Fingerprint;

        folder.WriteFile("growing/extra.md", "new file");

        Scanner.Scan(folder.Root).Skills.Single().Fingerprint.ShouldNotBe(before);
    }

    [Test]
    public void Fingerprint_changes_when_a_file_is_removed()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("shrinking");
        var extra = folder.WriteFile("shrinking/extra.md", "to be removed");
        var before = Scanner.Scan(folder.Root).Skills.Single().Fingerprint;

        File.Delete(extra);

        Scanner.Scan(folder.Root).Skills.Single().Fingerprint.ShouldNotBe(before);
    }

    [Test]
    public void Fingerprint_changes_when_a_file_is_renamed()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("renaming");
        var original = folder.WriteFile("renaming/old-name.md", "same bytes");
        var before = Scanner.Scan(folder.Root).Skills.Single().Fingerprint;

        File.Move(original, folder.Combine("renaming", "new-name.md"));

        Scanner.Scan(folder.Root).Skills.Single().Fingerprint.ShouldNotBe(before);
    }

    [Test]
    public void Identical_content_at_two_skill_paths_gives_equal_fingerprints()
    {
        using var folder = new TempSkillFolder();
        var first = folder.WriteSkill("first");
        var second = folder.WriteSkill("second");
        var fixedTime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, fixedTime);
        File.SetLastWriteTimeUtc(second, fixedTime);

        var skills = Scanner.Scan(folder.Root).Skills;

        skills.Count.ShouldBe(2);
        skills[0].Path.ShouldNotBe(skills[1].Path);
        skills[0].Fingerprint.ShouldBe(skills[1].Fingerprint);
    }

    [Test]
    public void Fingerprint_does_not_depend_on_the_absolute_folder_path()
    {
        using var firstRoot = new TempSkillFolder();
        using var secondRoot = new TempSkillFolder();
        var fixedTime = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var firstFile = firstRoot.WriteSkill("same");
        var secondFile = secondRoot.WriteSkill("same");
        File.SetLastWriteTimeUtc(firstFile, fixedTime);
        File.SetLastWriteTimeUtc(secondFile, fixedTime);

        var first = Scanner.Scan(firstRoot.Root).Skills.Single();
        var second = Scanner.Scan(secondRoot.Root).Skills.Single();

        first.Path.ShouldNotBe(second.Path);
        first.Fingerprint.ShouldBe(second.Fingerprint);
    }

    /// <summary>Creates a symbolic link, or skips the test when the platform refuses it.</summary>
    internal static void CreateLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("Symbolic links cannot be created on this platform or account: " + exception.Message);
        }
    }
}
