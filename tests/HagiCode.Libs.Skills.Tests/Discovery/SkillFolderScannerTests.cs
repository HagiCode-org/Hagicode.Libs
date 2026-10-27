using HagiCode.Libs.Skills.Discovery;

namespace HagiCode.Libs.Skills.Tests;

public sealed class SkillFolderScannerTests
{
    private static readonly SkillFolderScanner Scanner = new();

    [Test]
    public void Folder_with_skills_returns_one_skill_per_sub_folder_named_by_slug()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("alpha");
        folder.WriteSkill("beta");

        var result = Scanner.Scan(folder.Root);

        result.Succeeded.ShouldBeTrue();
        result.Truncated.ShouldBeFalse();
        result.Skills.Select(static skill => skill.Slug).ShouldBe(["alpha", "beta"]);
        result.Skills.ShouldAllBe(static skill => skill.Path.Length > 0 && Path.IsPathRooted(skill.Path));
        result.Skills.ShouldAllBe(static skill => skill.IsLoadable);
        result.Skills[0].Name.ShouldBe("demo");
    }

    [Test]
    public void Loose_files_hidden_folders_and_folders_without_skill_md_are_ignored()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("visible");
        folder.WriteFile("loose-notes.md", "not a skill");
        folder.WriteFile("no-skill-file/readme.md", "no SKILL.md here");
        folder.WriteSkill(".hidden");

        var result = Scanner.Scan(folder.Root);

        result.Succeeded.ShouldBeTrue();
        result.Skills.Select(static skill => skill.Slug).ShouldBe(["visible"]);
    }

    [Test]
    public void Scanned_folder_itself_is_not_a_skill()
    {
        using var folder = new TempSkillFolder();
        folder.WriteFile("SKILL.md", TempSkillFolder.ValidFrontMatter);

        var result = Scanner.Scan(folder.Root);

        result.Succeeded.ShouldBeTrue();
        result.Skills.ShouldBeEmpty();
    }

    [Test]
    public void Skill_two_levels_below_the_scanned_folder_is_not_found()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("outer/inner");

        var result = Scanner.Scan(folder.Root);

        result.Succeeded.ShouldBeTrue();
        result.Skills.ShouldBeEmpty();
    }

    [Test]
    public void Empty_folder_succeeds_with_an_empty_list()
    {
        using var folder = new TempSkillFolder();

        var result = Scanner.Scan(folder.Root);

        result.Succeeded.ShouldBeTrue();
        result.Skills.ShouldBeEmpty();
        result.Truncated.ShouldBeFalse();
    }

    [Test]
    public void Scanning_twice_without_changes_lists_skills_in_the_same_ordinal_slug_order()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("zeta");
        folder.WriteSkill("alpha");
        folder.WriteSkill("Mid");

        var first = Scanner.Scan(folder.Root);
        var second = Scanner.Scan(folder.Root);

        first.Skills.Select(static skill => skill.Slug).ShouldBe(["Mid", "alpha", "zeta"]);
        second.Skills.Select(static skill => skill.Slug).ShouldBe(first.Skills.Select(static skill => skill.Slug).ToArray());
    }

    [Test]
    public void Valid_slug_with_digits_dots_dashes_and_underscores_is_accepted()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("code-review_2");

        var result = Scanner.Scan(folder.Root);

        result.Skills.Single().Slug.ShouldBe("code-review_2");
        result.Skills.Single().Problems.ShouldBeEmpty();
        result.Skills.Single().IsLoadable.ShouldBeTrue();
    }

    [Test]
    public void Invalid_slugs_are_listed_with_an_invalid_slug_problem_and_are_not_loadable()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("my skill");
        folder.WriteSkill("valid");
        folder.WriteSkill(new string('a', 65));

        var result = Scanner.Scan(folder.Root);

        var invalid = result.Skills.Where(static skill => skill.Slug != "valid").ToList();
        invalid.Count.ShouldBe(2);
        foreach (var skill in invalid)
        {
            skill.IsLoadable.ShouldBeFalse();
            skill.Fingerprint.ShouldBeNull();
            skill.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.InvalidSlug]);
        }

        result.Skills.Single(static skill => skill.Slug == "valid").IsLoadable.ShouldBeTrue();
    }

    [Test]
    public void Slug_of_exactly_64_characters_is_accepted()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill(new string('a', 64));

        var result = Scanner.Scan(folder.Root);

        result.Skills.Single().IsLoadable.ShouldBeTrue();
    }

    [Test]
    public void Missing_name_and_description_are_listed_as_problems_and_not_loadable()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("no-name", "---\ndescription: has description\n---\n");
        folder.WriteSkill("no-description", "---\nname: has-name\n---\n");

        var result = Scanner.Scan(folder.Root);

        result.Skills.Select(static skill => skill.Slug).ShouldBe(["no-description", "no-name"]);
        result.Skills[0].Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.MissingDescription]);
        result.Skills[1].Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.MissingName]);
        result.Skills.ShouldAllBe(static skill => !skill.IsLoadable);
    }

    [Test]
    public void Unparseable_front_matter_is_reported_and_the_other_skills_still_scan()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("broken", "---\nname: [oops\n");
        folder.WriteSkill("fine");

        var result = Scanner.Scan(folder.Root);

        result.Succeeded.ShouldBeTrue();
        result.Skills.Single(static skill => skill.Slug == "broken").Problems
            .Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.UnreadableMetadata]);
        result.Skills.Single(static skill => skill.Slug == "fine").IsLoadable.ShouldBeTrue();
    }

    [Test]
    public void Oversized_front_matter_is_reported_as_oversized_metadata()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("huge", "---\nname: huge\ndescription: " + new string('x', 40 * 1024) + "\n---\n");

        var skill = Scanner.Scan(folder.Root).Skills.Single();

        skill.IsLoadable.ShouldBeFalse();
        skill.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.OversizedMetadata]);
    }

    [Test]
    public void Skill_with_more_than_500_files_is_too_large()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("crowded");
        for (var index = 0; index < SkillFolderLimits.MaxFilesPerSkill; index++)
        {
            folder.WriteFile($"crowded/file-{index:0000}.txt", "x");
        }

        var skill = Scanner.Scan(folder.Root).Skills.Single();

        skill.IsLoadable.ShouldBeFalse();
        skill.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.TooLarge]);
    }

    [Test]
    public void Skill_with_exactly_500_files_is_loadable()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("at-limit");
        for (var index = 0; index < SkillFolderLimits.MaxFilesPerSkill - 1; index++)
        {
            folder.WriteFile($"at-limit/file-{index:0000}.txt", "x");
        }

        var skill = Scanner.Scan(folder.Root).Skills.Single();

        skill.IsLoadable.ShouldBeTrue();
    }

    [Test]
    public void Skill_over_the_size_limit_is_too_large_and_one_at_the_limit_is_loadable()
    {
        using var folder = new TempSkillFolder();
        // Both SKILL.md files have the same length, so the data file sizes are exact.
        var skillLength = new FileInfo(folder.WriteSkill("at-limit")).Length;
        folder.WriteSkill("over-limit");
        SetLength(folder.Combine("at-limit", "data.bin"), SkillFolderLimits.MaxBytesPerSkill - skillLength);
        SetLength(folder.Combine("over-limit", "data.bin"), SkillFolderLimits.MaxBytesPerSkill - skillLength + 1);

        var result = Scanner.Scan(folder.Root);

        result.Skills.Single(static skill => skill.Slug == "at-limit").IsLoadable.ShouldBeTrue();
        var over = result.Skills.Single(static skill => skill.Slug == "over-limit");
        over.IsLoadable.ShouldBeFalse();
        over.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.TooLarge]);
    }

    [Test]
    public void Folder_with_more_than_200_skills_returns_the_first_200_by_slug_and_reports_truncation()
    {
        using var folder = new TempSkillFolder();
        for (var index = 0; index <= SkillFolderLimits.MaxSkillsPerFolder; index++)
        {
            folder.WriteSkill($"skill-{index:000}");
        }

        var result = Scanner.Scan(folder.Root);

        result.Succeeded.ShouldBeTrue();
        result.Truncated.ShouldBeTrue();
        result.Skills.Count.ShouldBe(SkillFolderLimits.MaxSkillsPerFolder);
        result.Skills[0].Slug.ShouldBe("skill-000");
        result.Skills[^1].Slug.ShouldBe("skill-199");
    }

    [Test]
    public void Missing_folder_fails_with_not_found()
    {
        using var folder = new TempSkillFolder();

        var result = Scanner.Scan(folder.Combine("absent"));

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldBe(SkillFolderScanFailure.NotFound);
        result.Skills.ShouldBeEmpty();
        result.Truncated.ShouldBeFalse();
    }

    [Test]
    public void Path_to_a_regular_file_fails_with_not_a_directory()
    {
        using var folder = new TempSkillFolder();
        var file = folder.WriteFile("plain.txt", "just a file");

        var result = Scanner.Scan(file);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldBe(SkillFolderScanFailure.NotADirectory);
        result.Skills.ShouldBeEmpty();
    }

    [Test]
    public void Folder_that_cannot_be_listed_fails_with_access_denied()
    {
        if (!IsPosixPermissionTestSupported())
        {
            Assert.Ignore("POSIX permissions are needed to make a folder unreadable.");
        }

        using var folder = new TempSkillFolder();
        folder.WriteSkill("hidden-from-scan");
        try
        {
            File.SetUnixFileMode(folder.Root, UnixFileMode.None);

            var result = Scanner.Scan(folder.Root);

            result.Succeeded.ShouldBeFalse();
            result.Failure.ShouldBe(SkillFolderScanFailure.AccessDenied);
            result.Skills.ShouldBeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(folder.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public void Scan_with_a_cancelled_token_throws_operation_canceled()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("alpha");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Scanner.Scan(folder.Root, cancellation.Token));
    }

    [Test]
    public async Task ScanAsync_with_a_cancelled_token_throws_operation_canceled()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("alpha");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var thrown = false;
        try
        {
            await Scanner.ScanAsync(folder.Root, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            thrown = true;
        }

        thrown.ShouldBeTrue();
    }

    [Test]
    public async Task ScanAsync_returns_the_same_result_as_Scan()
    {
        using var folder = new TempSkillFolder();
        folder.WriteSkill("alpha");
        folder.WriteSkill("beta");

        var async = await Scanner.ScanAsync(folder.Root);
        var sync = Scanner.Scan(folder.Root);

        async.Skills.Select(static skill => skill.Slug).ShouldBe(sync.Skills.Select(static skill => skill.Slug).ToArray());
        async.Skills.Select(static skill => skill.Fingerprint).ShouldBe(sync.Skills.Select(static skill => skill.Fingerprint).ToArray());
    }

    internal static bool IsPosixPermissionTestSupported() =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && Environment.UserName != "root";

    private static void SetLength(string path, long length)
    {
        using var stream = File.Create(path);
        stream.SetLength(length);
    }
}
