using System.Text;
using HagiCode.Libs.Skills.Discovery;

namespace HagiCode.Libs.Skills.Tests;

public sealed class SkillDocumentParserTests
{
    private static readonly SkillDocumentParser Parser = new();

    [Test]
    public void Valid_front_matter_reports_name_and_description()
    {
        var metadata = Parse("---\nname: code-review\ndescription: Reviews diffs\n---\n# Body\n");

        metadata.Name.ShouldBe("code-review");
        metadata.Description.ShouldBe("Reviews diffs");
        metadata.Problems.ShouldBeEmpty();
    }

    [Test]
    public void Valid_front_matter_accepts_a_byte_order_mark_and_crlf_line_endings()
    {
        var metadata = Parse("﻿---\r\nname: crlf\r\ndescription: Windows line endings\r\n---\r\nBody\r\n");

        metadata.Name.ShouldBe("crlf");
        metadata.Description.ShouldBe("Windows line endings");
        metadata.Problems.ShouldBeEmpty();
    }

    [Test]
    public void Missing_name_reports_missing_name_and_keeps_the_description()
    {
        var metadata = Parse("---\ndescription: Has no name\n---\n");

        metadata.Name.ShouldBeNull();
        metadata.Description.ShouldBe("Has no name");
        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.MissingName]);
        metadata.Problems[0].Message.ShouldContain("name");
    }

    [Test]
    public void Missing_description_reports_missing_description_and_keeps_the_name()
    {
        var metadata = Parse("---\nname: no-description\n---\n");

        metadata.Name.ShouldBe("no-description");
        metadata.Description.ShouldBeNull();
        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.MissingDescription]);
        metadata.Problems[0].Message.ShouldContain("description");
    }

    [Test]
    public void Empty_or_blank_values_count_as_missing()
    {
        var metadata = Parse("---\nname: \"   \"\ndescription: \"\"\n---\n");

        metadata.Name.ShouldBeNull();
        metadata.Description.ShouldBeNull();
        metadata.Problems.Select(static problem => problem.Code)
            .ShouldBe([SkillProblemCodes.MissingName, SkillProblemCodes.MissingDescription]);
    }

    [Test]
    public void Empty_front_matter_reports_both_fields_missing()
    {
        var metadata = Parse("---\n---\n# Body only\n");

        metadata.Problems.Select(static problem => problem.Code)
            .ShouldBe([SkillProblemCodes.MissingName, SkillProblemCodes.MissingDescription]);
    }

    [Test]
    public void File_without_front_matter_reports_both_fields_missing()
    {
        var metadata = Parse("# A skill with no front matter\n");

        metadata.Name.ShouldBeNull();
        metadata.Description.ShouldBeNull();
        metadata.Problems.Select(static problem => problem.Code)
            .ShouldBe([SkillProblemCodes.MissingName, SkillProblemCodes.MissingDescription]);
    }

    [Test]
    public void Unparseable_yaml_reports_unreadable_metadata()
    {
        var metadata = Parse("---\nname: [unclosed\ndescription: x\n---\n");

        metadata.Name.ShouldBeNull();
        metadata.Description.ShouldBeNull();
        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.UnreadableMetadata]);
    }

    [Test]
    public void Duplicate_keys_report_unreadable_metadata()
    {
        var metadata = Parse("---\nname: one\nname: two\ndescription: x\n---\n");

        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.UnreadableMetadata]);
    }

    [Test]
    public void Unclosed_front_matter_reports_unreadable_metadata()
    {
        var metadata = Parse("---\nname: open\ndescription: never closed\n");

        metadata.Name.ShouldBeNull();
        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.UnreadableMetadata]);
    }

    [Test]
    public void Oversized_front_matter_reports_oversized_metadata()
    {
        var metadata = Parse(
            "---\nname: big\ndescription: " + new string('a', 40 * 1024) + "\n---\n");

        metadata.Name.ShouldBeNull();
        metadata.Description.ShouldBeNull();
        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.OversizedMetadata]);
    }

    [Test]
    public void Front_matter_just_under_the_limit_is_accepted()
    {
        var metadata = Parse(
            "---\nname: near-limit\ndescription: " + new string('b', 20 * 1024) + "\n---\n");

        metadata.Name.ShouldBe("near-limit");
        metadata.Problems.ShouldBeEmpty();
    }

    [Test]
    public void Oversized_front_matter_is_not_read_past_the_limit()
    {
        var source = new ReadCountingStream(Encoding.UTF8.GetBytes(
            "---\nname: huge\ndescription: " + new string('c', 1024 * 1024) + "\n---\n"));

        var metadata = Parser.ParseContent(source);

        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.OversizedMetadata]);
        source.TotalBytesRead.ShouldBeLessThanOrEqualTo(SkillFolderLimits.MaxFrontMatterBytes);
    }

    [Test]
    public void Missing_file_reports_unreadable()
    {
        using var folder = new TempSkillFolder();

        var metadata = Parser.ParseFile(folder.Combine("absent", "SKILL.md"));

        metadata.Name.ShouldBeNull();
        metadata.Problems.Select(static problem => problem.Code).ShouldBe([SkillProblemCodes.Unreadable]);
    }

    [Test]
    public void Parse_file_reads_the_front_matter_from_disk()
    {
        using var folder = new TempSkillFolder();
        var path = folder.WriteFile("demo/SKILL.md", TempSkillFolder.ValidFrontMatter);

        var metadata = Parser.ParseFile(path);

        metadata.Name.ShouldBe("demo");
        metadata.Description.ShouldBe("A demo skill");
        metadata.Problems.ShouldBeEmpty();
    }

    private static SkillDocumentMetadata Parse(string text) =>
        Parser.ParseContent(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    /// <summary>A memory stream that records how many bytes the parser asked for.</summary>
    private sealed class ReadCountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long TotalBytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }
    }
}
