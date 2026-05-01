using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliExecutableResolverTests
{
    private const string AgentCliPathEnvironmentVariable = "HAGICODE_AGENT_CLI_PATH";

    [Fact]
    public void ResolveExecutablePath_prefers_hagicode_agent_cli_path_before_path()
    {
        using var sandbox = new DirectorySandbox();
        var executable = sandbox.CreateFile(Path.Combine("custom", "alpha"));
        sandbox.CreateFile(Path.Combine("path", "alpha"));
        var resolver = new CliExecutableResolver();

        var resolved = resolver.ResolveExecutablePath(
            "alpha",
            sandbox.BuildEnvironment(
                customPath: Path.Combine(sandbox.RootPath, "custom"),
                path: Path.Combine(sandbox.RootPath, "path")));

        resolved.ShouldBe(executable);
    }

    [Fact]
    public void ResolveExecutablePath_preserves_hagicode_agent_cli_path_directory_order()
    {
        using var sandbox = new DirectorySandbox();
        var firstMatch = sandbox.CreateFile(Path.Combine("custom-a", "alpha"));
        sandbox.CreateFile(Path.Combine("custom-b", "alpha"));
        var resolver = new CliExecutableResolver();

        var resolved = resolver.ResolveExecutablePath(
            "alpha",
            sandbox.BuildEnvironment(
                customPath: string.Join(
                    Path.PathSeparator,
                    [Path.Combine(sandbox.RootPath, "custom-a"), Path.Combine(sandbox.RootPath, "custom-b")]),
                path: Path.Combine(sandbox.RootPath, "path")));

        resolved.ShouldBe(firstMatch);
    }

    [Fact]
    public void ResolveExecutablePath_falls_back_to_path_when_hagicode_agent_cli_path_has_no_match()
    {
        using var sandbox = new DirectorySandbox();
        sandbox.CreateDirectory("custom");
        var executable = sandbox.CreateFile(Path.Combine("path", "alpha"));
        var resolver = new CliExecutableResolver();

        var resolved = resolver.ResolveExecutablePath(
            "alpha",
            sandbox.BuildEnvironment(
                customPath: Path.Combine(sandbox.RootPath, "custom"),
                path: Path.Combine(sandbox.RootPath, "path")));

        resolved.ShouldBe(executable);
    }

    [Fact]
    public void ResolveFirstAvailablePath_honors_candidate_order()
    {
        using var sandbox = new DirectorySandbox();
        sandbox.CreateFile("beta");
        sandbox.CreateFile("alpha");
        var resolver = new CliExecutableResolver();

        var resolved = resolver.ResolveFirstAvailablePath(["missing", "beta", "alpha"], sandbox.BuildEnvironment());

        resolved.ShouldEndWith("beta");
    }

    [Fact]
    public void ResolveExecutablePaths_returns_all_distinct_matches_in_probe_order()
    {
        using var sandbox = new DirectorySandbox();
        var resolver = new CliExecutableResolver();
        var duplicatePath = string.Join(Path.PathSeparator, [sandbox.RootPath, sandbox.RootPath]);
        var executable = sandbox.CreateFile("alpha");

        var resolved = resolver.ResolveExecutablePaths(
            "alpha",
            new Dictionary<string, string?> { ["PATH"] = duplicatePath });

        resolved.ShouldBe([executable]);
    }

    [Fact]
    public void IsExecutableAvailable_returns_false_when_missing()
    {
        using var sandbox = new DirectorySandbox();
        var resolver = new CliExecutableResolver();

        resolver.IsExecutableAvailable("missing", sandbox.BuildEnvironment()).ShouldBeFalse();
    }

    [Fact]
    public void ResolveExecutablePath_on_windows_tries_known_extensions_for_hagicode_agent_cli_path_entries()
    {
        using var sandbox = new DirectorySandbox();
        var executable = sandbox.CreateFile(Path.Combine("custom", "npm.cmd"));
        var resolver = new CliExecutableResolver(static () => true);

        var resolved = resolver.ResolveExecutablePath(
            "npm",
            sandbox.BuildEnvironment(
                customPath: Path.Combine(sandbox.RootPath, "custom"),
                pathExt: ".EXE;.CMD;.BAT"));

        resolved.ShouldBe(executable);
    }

    [Fact]
    public void ResolveExecutablePath_on_windows_probes_relative_paths_without_extensions_like_cliwrap()
    {
        using var sandbox = new DirectorySandbox();
        var executable = sandbox.CreateFile(Path.Combine("tools", "npm.cmd"));
        var resolver = new CliExecutableResolver(static () => true);
        var currentDirectory = Directory.GetCurrentDirectory();

        Directory.SetCurrentDirectory(sandbox.RootPath);
        try
        {
            var resolved = resolver.ResolveExecutablePath(Path.Combine("tools", "npm"), sandbox.BuildEnvironment(pathExt: ".EXE;.CMD;.BAT"));

            resolved.ShouldBe(executable);
        }
        finally
        {
            Directory.SetCurrentDirectory(currentDirectory);
        }
    }

    private sealed class DirectorySandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-resolver-{Guid.NewGuid():N}");

        public DirectorySandbox()
        {
            Directory.CreateDirectory(_root);
        }

        public string RootPath => _root;

        public string CreateFile(string relativePath)
        {
            var fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Empty);
            return fullPath;
        }

        public string CreateDirectory(string relativePath)
        {
            var fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        public IReadOnlyDictionary<string, string?> BuildEnvironment(
            string? customPath = null,
            string? path = null,
            string? pathExt = null)
        {
            return new Dictionary<string, string?>
            {
                [AgentCliPathEnvironmentVariable] = customPath,
                ["PATH"] = path ?? _root,
                ["PATHEXT"] = pathExt
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }
}
