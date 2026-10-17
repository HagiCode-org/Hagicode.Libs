namespace HagiCode.Libs.Git.Tests;

/// <summary>Task 3.4: behaviors that differ between operating systems, each assertion OS-neutral unless guarded.</summary>
[TestFixture]
public sealed class CrossOsBehaviorTests : GitTestBase
{
    [TestCase("false")]
    [TestCase("input")]
    [TestCase("true")]
    public async Task CRLF_content_committed_under_each_autocrlf_setting_leaves_a_clean_tree(string autocrlf)
    {
        var repository = NewRepositoryWithCommit();
        Git.Must(repository, "config", "core.autocrlf", autocrlf);
        WriteFile(repository, "windows.txt", "line one\r\nline two\r\n");
        await Client.StageAsync(repository, "windows.txt");
        var commit = await Client.CommitAsync(repository, "crlf");

        var probe = await Client.ProbeDirtyAsync(repository);

        Assert.That(commit.Success, Is.True, commit.Failure?.Message);
        Assert.That(probe.Value!.HasChanges, Is.False, "git normalizes line endings, so the tree must be clean");
    }

    [Test]
    public async Task Case_only_rename_is_reported_as_a_rename_on_every_file_system()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "case.txt", "x\n");
        Git.Must(repository, "add", "case.txt");
        Git.Must(repository, "commit", "--quiet", "-m", "case");
        Git.Must(repository, "mv", "case.txt", "CASE.txt");

        var status = await Client.GetStatusAsync(repository);

        var entry = status.Value!.Entries.Single();
        Assert.That(entry.Path, Is.EqualTo("CASE.txt"));
        Assert.That(entry.IndexChange, Is.EqualTo(GitChangeKind.Renamed));
    }

    [Test]
    public async Task Long_relative_paths_are_staged_and_reported_intact()
    {
        var repository = NewRepositoryWithCommit();
        var segments = Enumerable.Range(0, 4).Select(index => new string((char)('a' + index), 40)).ToArray();
        var relative = string.Join('/', segments) + "/long-name.txt";
        if (OperatingSystem.IsWindows() && repository.Length + relative.Length > 240)
        {
            Assert.Ignore("Windows MAX_PATH applies to this checkout location; the long-path case needs core.longpaths and a long Environment.Root.");
        }

        WriteFile(repository, relative, "long\n");

        var stage = await Client.StageAsync(repository, relative);
        var status = await Client.GetStatusAsync(repository);

        Assert.That(stage.Success, Is.True, stage.Failure?.Message);
        Assert.That(status.Value!.Entries.Single().Path, Is.EqualTo(relative));
    }

    [Test]
    public async Task Spaces_and_non_ascii_in_repository_and_file_names_round_trip()
    {
        var repository = NewRepository("仓库 with spaces");
        WriteFile(repository, "文件 名 (1).md", "hello\n");

        var stage = await Client.StageAllAsync(repository);
        var commit = await Client.CommitAsync(repository, "names");
        var probe = await Client.ProbeDirtyAsync(repository);

        Assert.That(stage.Value, Is.EqualTo(1));
        Assert.That(commit.Success, Is.True, commit.Failure?.Message);
        Assert.That(Git.Must(repository, "-c", "core.quotepath=false", "ls-files"), Is.EqualTo("文件 名 (1).md"));
        Assert.That(probe.Value!.HasChanges, Is.False);
    }

    [Test]
    public async Task Path_separators_are_normalized_to_forward_slashes()
    {
        var repository = NewRepositoryWithCommit();
        WriteFile(repository, "sub/file.txt", "x\n");
        var input = OperatingSystem.IsWindows() ? "sub\\file.txt" : "sub/file.txt";

        var stage = await Client.StageAsync(repository, input);
        var status = await Client.GetStatusAsync(repository);

        Assert.That(stage.Success, Is.True, stage.Failure?.Message);
        Assert.That(status.Value!.Entries.Single().Path, Is.EqualTo("sub/file.txt"));
    }
}
