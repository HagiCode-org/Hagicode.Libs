using System.Runtime.Versioning;

namespace HagiCode.Libs.Git.Tests;

/// <summary>
/// Task 1.6: <see cref="IGitClient.RemoveRepositoryAsync" /> against real repositories inside the temporary test root only.
/// Links and permission cases skip themselves where the platform or the account cannot create or enforce them.
/// </summary>
[TestFixture]
public sealed class RepositoryRemovalTests : GitTestBase
{
    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task Clean_repository_in_sync_with_its_upstream_is_removed()
    {
        var repository = NewPublishedRepository();

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(result.Value!.RepositoryPath, Is.EqualTo(Path.GetFullPath(repository)));
        Assert.That(Directory.Exists(repository), Is.False);
    }

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task Dirty_repository_is_refused_and_nothing_is_deleted()
    {
        var repository = NewPublishedRepository();
        WriteFile(repository, "untracked.txt", "keep\n");

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.WorkingTreeDirty));
        Assert.That(File.ReadAllText(Path.Combine(repository, "untracked.txt")), Is.EqualTo("keep\n"));
        Assert.That(File.Exists(Path.Combine(repository, "README.md")), Is.True);
    }

    [Test]
    public async Task Commits_ahead_of_the_upstream_are_refused()
    {
        var repository = NewPublishedRepository();
        CommitLocal(repository, "local-only.txt");

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.UnpushedCommits));
        Assert.That(Directory.Exists(repository), Is.True);
    }

    [Test]
    public async Task Repository_without_an_upstream_is_refused()
    {
        var repository = NewRepositoryWithCommit($"local-{Guid.NewGuid():N}");

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.UnpushedCommits));
        Assert.That(Directory.Exists(repository), Is.True);
    }

    [Test]
    public async Task Detached_head_is_refused()
    {
        var repository = NewPublishedRepository();
        Git.Must(repository, "checkout", "--quiet", "--detach");

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.UnpushedCommits));
        Assert.That(Directory.Exists(repository), Is.True);
    }

    [Test]
    public async Task Inconclusive_status_is_refused_and_nothing_is_deleted()
    {
        var repository = NewPublishedRepository();
        // A corrupt index makes git status fail, so the probe cannot prove the tree is clean.
        File.WriteAllText(Path.Combine(repository, ".git", "index"), "not an index");

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.CommandFailed));
        Assert.That(File.Exists(Path.Combine(repository, "README.md")), Is.True);
    }

    [Test]
    public async Task Force_removes_a_dirty_repository()
    {
        var repository = NewPublishedRepository();
        WriteFile(repository, "untracked.txt", "discard\n");

        var result = await Client.RemoveRepositoryAsync(repository, new GitRemoveOptions(Force: true));

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(Directory.Exists(repository), Is.False);
    }

    [Test]
    public async Task Force_removes_a_repository_with_commits_ahead_of_the_upstream()
    {
        var repository = NewPublishedRepository();
        CommitLocal(repository, "local-only.txt");

        var result = await Client.RemoveRepositoryAsync(repository, new GitRemoveOptions(Force: true));

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(Directory.Exists(repository), Is.False);
    }

    [Test]
    public async Task Force_removes_a_repository_without_an_upstream()
    {
        var repository = NewRepositoryWithCommit($"local-{Guid.NewGuid():N}");

        var result = await Client.RemoveRepositoryAsync(repository, new GitRemoveOptions(Force: true));

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(Directory.Exists(repository), Is.False);
    }

    [Test]
    public async Task Linked_worktree_is_refused_even_with_force()
    {
        var repository = NewRepositoryWithCommit($"main-{Guid.NewGuid():N}");
        var worktree = Path.Combine(Environment.Root, "linked-worktree");
        Git.Must(repository, "worktree", "add", "--detach", worktree);

        var result = await Client.RemoveRepositoryAsync(worktree, new GitRemoveOptions(Force: true));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
        Assert.That(File.Exists(Path.Combine(worktree, ".git")), Is.True, "the worktree is not deleted");
        Assert.That(Directory.Exists(repository), Is.True, "the main repository is not touched");
    }

    [Test]
    public async Task Submodule_is_refused_even_with_force()
    {
        var parent = NewRepositoryWithCommit($"parent-{Guid.NewGuid():N}");
        var source = NewRepositoryWithCommit($"module-{Guid.NewGuid():N}");
        Git.Must(parent, "-c", "protocol.file.allow=always", "submodule", "add", "--quiet", source, "sub");
        var submodule = Path.Combine(parent, "sub");
        Assert.That(File.Exists(Path.Combine(submodule, ".git")), Is.True, "a submodule keeps a .git file, not a directory");

        var result = await Client.RemoveRepositoryAsync(submodule, new GitRemoveOptions(Force: true));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
        Assert.That(File.Exists(Path.Combine(submodule, ".git")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(parent, ".git", "modules", "sub")), Is.True, "the parent's module data survives");
    }

    [Test]
    public async Task Subdirectory_of_a_repository_is_refused()
    {
        var repository = NewPublishedRepository();
        WriteFile(repository, "folder/file.txt", "keep\n");

        var result = await Client.RemoveRepositoryAsync(Path.Combine(repository, "folder"), new GitRemoveOptions(Force: true));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
        Assert.That(File.Exists(Path.Combine(repository, "folder", "file.txt")), Is.True);
        Assert.That(Directory.Exists(repository), Is.True);
    }

    [Test]
    public async Task Plain_non_repository_directory_is_refused_and_nothing_is_deleted()
    {
        var folder = Path.Combine(Environment.Root, "plain-folder");
        WriteFile(folder, "notes.txt", "keep\n");

        var result = await Client.RemoveRepositoryAsync(folder, new GitRemoveOptions(Force: true));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
        Assert.That(File.Exists(Path.Combine(folder, "notes.txt")), Is.True);
    }

    [Test]
    public async Task Filesystem_root_is_refused_even_with_force()
    {
        var root = Path.GetPathRoot(Environment.Root)!;

        var result = await Client.RemoveRepositoryAsync(root, new GitRemoveOptions(Force: true));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
    }

    [Test]
    public async Task Symlink_target_is_refused_and_the_repository_it_points_to_survives()
    {
        var repository = NewPublishedRepository();
        var link = Path.Combine(Environment.Root, "repo-link");
        if (!TryCreateDirectoryLink(link, repository))
        {
            Assert.Ignore("This account cannot create directory symbolic links.");
        }

        var result = await Client.RemoveRepositoryAsync(link, new GitRemoveOptions(Force: true));

        Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.InvalidRepository));
        Assert.That(Directory.Exists(repository), Is.True);
        Assert.That(File.Exists(Path.Combine(repository, "README.md")), Is.True);
    }

    [Test]
    public async Task Symlink_inside_a_removed_repository_is_removed_as_a_link_and_its_target_survives()
    {
        var outside = Path.Combine(Environment.Root, "outside");
        WriteFile(outside, "keep.txt", "outside content\n");
        var repository = NewPublishedRepository();
        if (!TryCreateDirectoryLink(Path.Combine(repository, "linked-dir"), outside)
            || !TryCreateFileLink(Path.Combine(repository, "linked-file.txt"), Path.Combine(outside, "keep.txt")))
        {
            Assert.Ignore("This account cannot create symbolic links.");
        }

        // Committed and pushed, so the checkout is clean and in sync and the removal is not refused for another reason.
        Git.Must(repository, "add", "linked-dir", "linked-file.txt");
        Git.Must(repository, "commit", "--quiet", "-m", "links");
        Git.Must(repository, "push", "--quiet");

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(Directory.Exists(repository), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(outside, "keep.txt")), Is.EqualTo("outside content\n"));
        Assert.That(Directory.Exists(outside), Is.True);
    }

    [Test]
    public async Task Read_only_files_are_deleted()
    {
        var repository = NewPublishedRepository();
        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }

        var result = await Client.RemoveRepositoryAsync(repository);

        Assert.That(result.Success, Is.True, result.Failure?.Message);
        Assert.That(Directory.Exists(repository), Is.False);
    }

    [Test]
    public async Task Cancelled_removal_throws_and_deletes_nothing()
    {
        var repository = NewPublishedRepository();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var cancelled = false;
        try
        {
            await Client.RemoveRepositoryAsync(repository, new GitRemoveOptions(Force: true), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.That(cancelled, Is.True, "cancellation is raised, not reported as a result");
        Assert.That(File.Exists(Path.Combine(repository, "README.md")), Is.True, "no entry is deleted after cancellation");
    }

    [Test]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task Partial_failure_is_reported_as_remove_failed_with_the_path()
    {
        // Needs a directory the account cannot write to. Root bypasses that, and Windows locks differently, so the test skips there.
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Directory write permissions are not enforced the same way on Windows.");
        }

        var repository = NewPublishedRepository();
        var locked = Path.Combine(repository, "locked");
        WriteFile(locked, "trapped.txt", "stuck\n");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (CanWriteInto(locked))
            {
                Assert.Ignore("The account can write into a read-only directory, so a deletion failure cannot be produced.");
            }

            var result = await Client.RemoveRepositoryAsync(repository, new GitRemoveOptions(Force: true));

            Assert.That(result.Failure!.Code, Is.EqualTo(GitFailureCode.RemoveFailed));
            Assert.That(result.Failure.Message, Does.Contain("trapped.txt"));
            Assert.That(File.Exists(Path.Combine(locked, "trapped.txt")), Is.True, "the file that could not be deleted is still there");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A repository with one commit, pushed to a bare remote as <c>origin/main</c>, so it is clean and in sync.</summary>
    private string NewPublishedRepository()
    {
        var remote = NewBareRemote($"remote-{Guid.NewGuid():N}.git");
        var repository = NewRepositoryWithCommit($"checkout-{Guid.NewGuid():N}");
        Git.Must(repository, "remote", "add", "origin", remote);
        Git.Must(repository, "push", "--quiet", "-u", "origin", "main");
        return repository;
    }

    private void CommitLocal(string repository, string file)
    {
        WriteFile(repository, file, "x\n");
        Git.Must(repository, "add", file);
        Git.Must(repository, "commit", "--quiet", "-m", file);
    }

    private static bool CanWriteInto(string directory)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "probe.txt"), "probe");
            File.Delete(Path.Combine(directory, "probe.txt"));
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
