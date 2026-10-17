namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 2.6: ensure-clone cases and bounded repository scanning, ported from the backend scan semantics.</summary>
[TestFixture]
public sealed class CloneAndScanTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task EnsureClone_clones_into_an_absent_target()
    {
        var remote = NewBareRemote();
        var seed = NewRepositoryWithCommit();
        Git.Must(seed, "remote", "add", "origin", remote);
        Git.Must(seed, "push", "--quiet", "-u", "origin", "main");
        var target = Path.Combine(Environment.Root, "clones", "project");

        var result = await Client.EnsureCloneAsync(remote, target);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value!.ReusedExistingRepository, Is.False);
        Assert.That(Path.GetFullPath(result.Value.RepositoryPath), Is.EqualTo(Path.GetFullPath(target)));
        Assert.That(File.Exists(Path.Combine(target, "README.md")), Is.True);
    }

    [Test]
    public async Task EnsureClone_reuses_a_repository_whose_origin_matches_after_normalization()
    {
        var remote = NewBareRemote();
        var target = Path.Combine(Environment.Root, "existing");
        Git.Must(Environment.Root, "clone", "--quiet", remote, target);

        var result = await Client.EnsureCloneAsync(remote + "/", target);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value!.ReusedExistingRepository, Is.True);
    }

    [Test]
    public async Task EnsureClone_treats_an_empty_folder_as_absent()
    {
        var remote = NewBareRemote();
        var seed = NewRepositoryWithCommit();
        Git.Must(seed, "remote", "add", "origin", remote);
        Git.Must(seed, "push", "--quiet", "-u", "origin", "main");
        var target = Path.Combine(Environment.Root, "empty-target");
        Directory.CreateDirectory(target);

        var result = await Client.EnsureCloneAsync(remote, target);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value!.ReusedExistingRepository, Is.False);
    }

    [Test]
    public async Task EnsureClone_conflicts_with_a_file_and_leaves_it_untouched()
    {
        var target = Path.Combine(Environment.Root, "a-file");
        File.WriteAllText(target, "keep me\n");

        var result = await Client.EnsureCloneAsync("https://example.invalid/x.git", target);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.CloneConflict));
        Assert.That(File.ReadAllText(target), Is.EqualTo("keep me\n"));
    }

    [Test]
    public async Task EnsureClone_conflicts_with_a_non_empty_folder_and_deletes_nothing()
    {
        var target = Path.Combine(Environment.Root, "busy");
        WriteFile(target, "notes.txt", "keep\n");

        var result = await Client.EnsureCloneAsync("https://example.invalid/x.git", target);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.CloneConflict));
        Assert.That(File.ReadAllText(Path.Combine(target, "notes.txt")), Is.EqualTo("keep\n"));
    }

    [Test]
    public async Task EnsureClone_conflicts_when_the_existing_origin_is_different()
    {
        var remote = NewBareRemote();
        var other = NewBareRemote("other.git");
        var target = Path.Combine(Environment.Root, "wrong-origin");
        Git.Must(Environment.Root, "clone", "--quiet", other, target);

        var result = await Client.EnsureCloneAsync(remote, target);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.CloneConflict));
        Assert.That(result.Failure.Message, Does.Contain("Expected"));
        Assert.That(result.Failure.Message, Does.Contain(other));
        Assert.That(Directory.Exists(Path.Combine(target, ".git")), Is.True, "the existing repository is not deleted");
    }

    [Test]
    public async Task EnsureClone_failure_leaves_no_partial_folder_behind()
    {
        var target = Path.Combine(Environment.Root, "never-cloned");
        var missingRemote = Path.Combine(Environment.Root, "missing.git");

        var result = await Client.EnsureCloneAsync(missingRemote, target);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.CloneFailed));
        Assert.That(Directory.Exists(target), Is.False);
    }

    [Test]
    public async Task Scan_respects_depth_and_exclusion_and_does_not_descend_into_repositories()
    {
        var root = Path.Combine(Environment.Root, "scan-root");
        CreateRepositoryFolder(Path.Combine(root, "a", "repo1"));
        CreateRepositoryFolder(Path.Combine(root, "b", "c", "d", "too-deep"));
        CreateRepositoryFolder(Path.Combine(root, "skipme", "excluded"));
        CreateRepositoryFolder(Path.Combine(root, ".hidden", "hidden-repo"));
        CreateRepositoryFolder(Path.Combine(root, "outer", "inner"));
        CreateRepositoryFolder(Path.Combine(root, "outer"));

        var result = await Client.ScanForRepositoriesAsync(root, new GitScanOptions(
            MaxDepth: 2,
            ExcludePatterns: ["skipme"]));

        var found = result.Value!.Select(Path.GetFullPath).ToList();
        Assert.That(found, Does.Contain(Path.GetFullPath(Path.Combine(root, "a", "repo1"))));
        Assert.That(found, Does.Contain(Path.GetFullPath(Path.Combine(root, "outer"))));
        Assert.That(found, Has.None.Matches<string>(path => path.EndsWith("too-deep")));
        Assert.That(found, Has.None.Matches<string>(path => path.EndsWith("excluded")));
        Assert.That(found, Has.None.Matches<string>(path => path.EndsWith("hidden-repo")));
        Assert.That(found, Has.None.Matches<string>(path => path.EndsWith("inner")),
            "a nested repository inside another repository is not returned");
    }

    [Test]
    public async Task Scan_stops_at_the_per_level_folder_limit()
    {
        var root = Path.Combine(Environment.Root, "limited");
        for (var index = 0; index < 3; index++)
        {
            CreateRepositoryFolder(Path.Combine(root, $"repo{index}"));
        }

        var result = await Client.ScanForRepositoriesAsync(root, new GitScanOptions(MaxFoldersPerLevel: 1));

        Assert.That(result.Value!, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Scan_fails_for_a_missing_root()
    {
        var result = await Client.ScanForRepositoriesAsync(Path.Combine(Environment.Root, "nope"));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidArgument));
    }

    private static void CreateRepositoryFolder(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, ".git"));
    }
}
