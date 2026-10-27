using System.Security.Cryptography;
using HagiCode.Libs.Skills.Discovery;

namespace HagiCode.Libs.Skills.Tests;

public sealed class SkillFolderScannerReadOnlyTests
{
    private static readonly SkillFolderScanner Scanner = new();

    [Test]
    public void Scan_leaves_the_folder_tree_unchanged_for_every_outcome()
    {
        using var folder = new TempSkillFolder();
        using var outside = new TempSkillFolder();
        outside.WriteFile("secret.md", "outside");
        BuildMixedFixture(folder, outside);
        var before = Snapshot(folder.Root);

        var success = Scanner.Scan(folder.Root);
        success.Succeeded.ShouldBeTrue();
        Snapshot(folder.Root).ShouldBe(before);

        Scanner.Scan(folder.Combine("absent")).Failure.ShouldBe(SkillFolderScanFailure.NotFound);
        Snapshot(folder.Root).ShouldBe(before);

        Scanner.Scan(folder.Combine("loose.txt")).Failure.ShouldBe(SkillFolderScanFailure.NotADirectory);
        Snapshot(folder.Root).ShouldBe(before);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Scanner.Scan(folder.Root, cancellation.Token));
        Snapshot(folder.Root).ShouldBe(before);

        Snapshot(outside.Root).ShouldContain("secret.md");
    }

    [Test]
    public void Access_denied_scan_leaves_the_folder_tree_unchanged()
    {
        if (!SkillFolderScannerTests.IsPosixPermissionTestSupported())
        {
            Assert.Ignore("POSIX permissions are needed to make a folder unreadable.");
        }

        using var folder = new TempSkillFolder();
        folder.WriteSkill("alpha");
        folder.WriteFile("alpha/notes.md", "notes");
        var before = Snapshot(folder.Root);

        try
        {
            File.SetUnixFileMode(folder.Root, UnixFileMode.None);
            Scanner.Scan(folder.Root).Failure.ShouldBe(SkillFolderScanFailure.AccessDenied);
        }
        finally
        {
            File.SetUnixFileMode(folder.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Snapshot(folder.Root).ShouldBe(before);
    }

    private static void BuildMixedFixture(TempSkillFolder folder, TempSkillFolder outside)
    {
        folder.WriteSkill("alpha");
        folder.WriteFile("alpha/notes.md", "notes");
        folder.WriteSkill("bad-yaml", "---\nname: [oops\n");
        folder.WriteSkill("my skill");
        folder.WriteSkill("nested/inner");
        folder.WriteSkill(".hidden");
        folder.WriteFile("loose.txt", "loose file");
        CreateLinkOrSkip(folder.Combine("alpha", "outside.md"), outside.Combine("secret.md"));
        CreateLinkOrSkip(folder.Combine("escape"), outside.Root);
        CreateLinkOrSkip(folder.Combine("alias"), folder.Combine("alpha"));
    }

    private static void CreateLinkOrSkip(string link, string target) =>
        SkillFolderScannerLinkAndFingerprintTests.CreateLink(link, target);

    /// <summary>Lists every entry below the root with its kind, size, timestamp and content hash.</summary>
    private static string Snapshot(string root)
    {
        var lines = new List<string>();
        AppendEntries(root, string.Empty, lines);
        return string.Join("\n", lines);
    }

    private static void AppendEntries(string folder, string prefix, List<string> lines)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder).OrderBy(entry => Path.GetFileName(entry), StringComparer.Ordinal))
        {
            var relative = prefix + Path.GetFileName(entry);
            var info = new FileInfo(entry);
            if (info.LinkTarget is { } target)
            {
                lines.Add($"L {relative} -> {target}");
            }
            else if (Directory.Exists(entry))
            {
                lines.Add($"D {relative} {new DirectoryInfo(entry).LastWriteTimeUtc.Ticks}");
                AppendEntries(entry, relative + "/", lines);
            }
            else
            {
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
                lines.Add($"F {relative} {info.Length} {info.LastWriteTimeUtc.Ticks} {hash}");
            }
        }
    }
}
