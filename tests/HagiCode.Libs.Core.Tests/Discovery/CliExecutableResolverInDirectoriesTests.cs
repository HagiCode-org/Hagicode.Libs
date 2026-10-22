using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliExecutableResolverInDirectoriesTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"resolver in dirs {Guid.NewGuid():N}");

    [SetUp]
    public void SetUp() => Directory.CreateDirectory(_root);

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void ResolveExecutablePathsInDirectories_on_unix_returns_the_bare_name_in_directory_order()
    {
        var first = CreateFile("first", "tool");
        var second = CreateFile("second", "tool");
        var resolver = new CliExecutableResolver(() => false);

        var resolved = resolver.ResolveExecutablePathsInDirectories(
            "tool",
            [Path.Combine(_root, "second"), Path.Combine(_root, "first")],
            environmentVariables: null);

        resolved.ShouldBe([second, first]);
    }

    [Test]
    public void ResolveExecutablePathsInDirectories_skips_missing_directories_and_collapses_duplicates()
    {
        var tool = CreateFile("bin", "tool");
        var resolver = new CliExecutableResolver(() => false);

        var resolved = resolver.ResolveExecutablePathsInDirectories(
            "tool",
            [Path.Combine(_root, "missing"), Path.Combine(_root, "bin"), Path.Combine(_root, "bin")],
            environmentVariables: null);

        resolved.ShouldBe([tool]);
    }

    [Test]
    public void ResolveExecutablePathsInDirectories_ignores_path_and_hagicode_agent_cli_path()
    {
        var onPath = CreateFile("on-path", "tool");
        var resolver = new CliExecutableResolver(() => false);

        var resolved = resolver.ResolveExecutablePathsInDirectories(
            "tool",
            [Path.Combine(_root, "empty")],
            new Dictionary<string, string?>
            {
                ["PATH"] = Path.Combine(_root, "on-path"),
                ["HAGICODE_AGENT_CLI_PATH"] = Path.Combine(_root, "on-path")
            });

        resolved.ShouldBeEmpty();
        resolved.ShouldNotContain(onPath);
    }

    [Test]
    public void ResolveExecutablePathsInDirectories_on_windows_follows_pathext_order()
    {
        var exe = CreateFile("win", "tool.exe");
        var cmd = CreateFile("win", "tool.cmd");
        var resolver = new CliExecutableResolver(() => true);

        var resolved = resolver.ResolveExecutablePathsInDirectories(
            "tool",
            [Path.Combine(_root, "win")],
            new Dictionary<string, string?> { ["PATHEXT"] = ".EXE;.CMD" });

        resolved.ShouldBe([exe, cmd]);
    }

    [Test]
    public void ResolveExecutablePathsInDirectories_on_windows_uses_default_extensions_when_pathext_is_absent()
    {
        var bat = CreateFile("win", "tool.bat");
        var resolver = new CliExecutableResolver(() => true);

        var resolved = resolver.ResolveExecutablePathsInDirectories("tool", [Path.Combine(_root, "win")], environmentVariables: null);

        resolved.ShouldBe([bat]);
    }

    [Test]
    public void ResolveExecutablePathsInDirectories_on_windows_prefers_exe_over_cmd_inside_one_directory_and_keeps_caller_order()
    {
        var cmd = CreateFile("first", "tool.cmd");
        var exe = CreateFile("first", "tool.exe");
        var second = CreateFile("second", "tool.cmd");
        var resolver = new CliExecutableResolver(() => true);

        var resolved = resolver.ResolveExecutablePathsInDirectories(
            "tool",
            [Path.Combine(_root, "first"), Path.Combine(_root, "second")],
            new Dictionary<string, string?> { ["PATHEXT"] = ".CMD;.EXE" });

        resolved.ShouldBe([exe, cmd, second]);
    }

    [Test]
    public void ResolveExecutablePathsInDirectories_on_windows_puts_the_fixed_extensions_before_pathext_extras()
    {
        var com = CreateFile("win", "tool.com");
        var bat = CreateFile("win", "tool.bat");
        var cmd = CreateFile("win", "tool.cmd");
        var resolver = new CliExecutableResolver(() => true);

        var resolved = resolver.ResolveExecutablePathsInDirectories(
            "tool",
            [Path.Combine(_root, "win")],
            new Dictionary<string, string?> { ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD" });

        resolved.ShouldBe([cmd, bat, com]);
    }

    private string CreateFile(string directoryName, string fileName)
    {
        var directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "#!/bin/sh\n");
        return Path.GetFullPath(path);
    }
}
