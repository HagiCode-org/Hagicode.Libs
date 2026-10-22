using HagiCode.Libs.Git.Internal;

namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 2.2 parser coverage: captured porcelain v2 samples, with no git process involved.</summary>
[TestFixture]
[Category(TestTiers.Integration)]
public sealed class PorcelainParserTests
{
    [Test]
    public void Parse_reads_the_branch_and_detached_head_as_null()
    {
        var attached = GitPorcelainV2Parser.Parse("# branch.oid abc\0# branch.head main\0");
        var detached = GitPorcelainV2Parser.Parse("# branch.oid abc\0# branch.head (detached)\0");

        Assert.That(attached.Branch, Is.EqualTo("main"));
        Assert.That(detached.Branch, Is.Null);
    }

    [Test]
    public void Parse_reads_staged_unstaged_and_untracked_entries()
    {
        var output = "# branch.head main\0"
            + "1 M. N... 100644 100644 100644 aaa bbb staged.txt\0"
            + "1 .M N... 100644 100644 100644 aaa bbb unstaged.txt\0"
            + "1 MM N... 100644 100644 100644 aaa bbb both.txt\0"
            + "? untracked.txt\0";

        var entries = GitPorcelainV2Parser.Parse(output).Entries;

        Assert.That(entries.Select(entry => entry.Path), Is.EqualTo(new[] { "staged.txt", "unstaged.txt", "both.txt", "untracked.txt" }));
        Assert.That(entries[0].IndexChange, Is.EqualTo(GitChangeKind.Modified));
        Assert.That(entries[0].WorkingTreeChange, Is.EqualTo(GitChangeKind.None));
        Assert.That(entries[1].WorkingTreeChange, Is.EqualTo(GitChangeKind.Modified));
        Assert.That(entries[3].WorkingTreeChange, Is.EqualTo(GitChangeKind.Untracked));
        Assert.That(entries[3].IndexChange, Is.EqualTo(GitChangeKind.None));
    }

    [Test]
    public void Parse_takes_a_rename_path_and_consumes_the_original_path_segment()
    {
        var output = "# branch.head main\0"
            + "2 R. N... 100644 100644 100644 aaa bbb R100 new name.txt\0old name.txt\0"
            + "? after.txt\0";

        var entries = GitPorcelainV2Parser.Parse(output).Entries;

        Assert.That(entries, Has.Count.EqualTo(2), "the original path is not an entry of its own");
        Assert.That(entries[0].Path, Is.EqualTo("new name.txt"));
        Assert.That(entries[0].OriginalPath, Is.EqualTo("old name.txt"));
        Assert.That(entries[0].IndexChange, Is.EqualTo(GitChangeKind.Renamed));
        Assert.That(entries[1].Path, Is.EqualTo("after.txt"));
    }

    [Test]
    public void Parse_keeps_non_ascii_and_spaced_paths_unmangled()
    {
        var output = "# branch.head main\0"
            + "1 .M N... 100644 100644 100644 aaa bbb dir with space/中文 文件.md\0"
            + "? 目录/新文件.txt\0";

        var entries = GitPorcelainV2Parser.Parse(output).Entries;

        Assert.That(entries[0].Path, Is.EqualTo("dir with space/中文 文件.md"));
        Assert.That(entries[1].Path, Is.EqualTo("目录/新文件.txt"));
    }

    [Test]
    public void Parse_reports_an_unmerged_entry_on_both_sides()
    {
        var output = "# branch.head main\0"
            + "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflicted.txt\0";

        var entry = GitPorcelainV2Parser.Parse(output).Entries.Single();

        Assert.That(entry.Path, Is.EqualTo("conflicted.txt"));
        Assert.That(entry.IndexChange, Is.EqualTo(GitChangeKind.Unmerged));
        Assert.That(entry.WorkingTreeChange, Is.EqualTo(GitChangeKind.Unmerged));
    }

    [Test]
    public void Parse_returns_an_empty_status_for_empty_output()
    {
        var status = GitPorcelainV2Parser.Parse(string.Empty);

        Assert.That(status.Branch, Is.Null);
        Assert.That(status.Entries, Is.Empty);
    }

    [Test]
    public void StageAllTargets_follow_the_backend_rule()
    {
        var entries = GitPorcelainV2Parser.Parse(
            "# branch.head main\0"
            + "1 .M N... 100644 100644 100644 aaa bbb worktree-only.txt\0"
            + "1 M. N... 100644 100644 100644 aaa bbb index-only.txt\0"
            + "1 MM N... 100644 100644 100644 aaa bbb both.txt\0"
            + "? untracked.txt\0").Entries;

        var targets = GitPorcelainV2Parser.StageAllTargets(entries);

        Assert.That(targets, Is.EqualTo(new[] { "worktree-only.txt", "untracked.txt" }));
    }

    [Test]
    public void UnstageAllTargets_follow_the_backend_rule_and_use_the_new_rename_path()
    {
        var entries = GitPorcelainV2Parser.Parse(
            "# branch.head main\0"
            + "1 .M N... 100644 100644 100644 aaa bbb worktree-only.txt\0"
            + "1 M. N... 100644 100644 100644 aaa bbb index-only.txt\0"
            + "1 MM N... 100644 100644 100644 aaa bbb both.txt\0"
            + "2 R. N... 100644 100644 100644 aaa bbb R100 renamed.txt\0original.txt\0"
            + "? untracked.txt\0").Entries;

        var targets = GitPorcelainV2Parser.UnstageAllTargets(entries);

        Assert.That(targets, Is.EqualTo(new[] { "index-only.txt", "both.txt", "renamed.txt" }));
    }

    [Test]
    public void Parse_reads_upstream_and_ahead_behind_counts()
    {
        var status = GitPorcelainV2Parser.Parse("# branch.oid abc\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +2 -1\0");

        Assert.That(status.Upstream, Is.EqualTo("origin/main"));
        Assert.That(status.Ahead, Is.EqualTo(2));
        Assert.That(status.Behind, Is.EqualTo(1));
    }

    [Test]
    public void Parse_reports_an_upstream_without_counts_when_branch_ab_is_absent()
    {
        var status = GitPorcelainV2Parser.Parse("# branch.oid abc\0# branch.head main\0# branch.upstream origin/gone\0");

        Assert.That(status.Upstream, Is.EqualTo("origin/gone"));
        Assert.That(status.Ahead, Is.Null);
        Assert.That(status.Behind, Is.Null);
    }

    [Test]
    public void Parse_reports_no_upstream_when_none_is_configured()
    {
        var status = GitPorcelainV2Parser.Parse("# branch.oid abc\0# branch.head main\0");

        Assert.That(status.Upstream, Is.Null);
        Assert.That(status.Ahead, Is.Null);
        Assert.That(status.Behind, Is.Null);
    }

    [Test]
    public void Parse_reads_an_unborn_branch_name_without_an_oid()
    {
        var status = GitPorcelainV2Parser.Parse("# branch.oid (initial)\0# branch.head main\0");

        Assert.That(status.Branch, Is.EqualTo("main"));
        Assert.That(status.Upstream, Is.Null);
        Assert.That(status.Entries, Is.Empty);
    }

    [Test]
    public void Parse_reports_a_detached_head_with_no_branch_and_no_upstream()
    {
        var status = GitPorcelainV2Parser.Parse("# branch.oid abc\0# branch.head (detached)\0");

        Assert.That(status.Branch, Is.Null);
        Assert.That(status.Upstream, Is.Null);
    }
}
