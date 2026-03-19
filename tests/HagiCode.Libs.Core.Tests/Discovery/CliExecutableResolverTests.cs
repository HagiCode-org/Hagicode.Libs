using Shouldly;
using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliExecutableResolverTests
{
    [Fact]
    public void ResolveExecutablePath_uses_custom_path_environment()
    {
        using var sandbox = new DirectorySandbox();
        var executable = sandbox.CreateFile("alpha");
        var resolver = new CliExecutableResolver();

        var resolved = resolver.ResolveExecutablePath("alpha", sandbox.BuildEnvironment());

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
    public void IsExecutableAvailable_returns_false_when_missing()
    {
        using var sandbox = new DirectorySandbox();
        var resolver = new CliExecutableResolver();

        resolver.IsExecutableAvailable("missing", sandbox.BuildEnvironment()).ShouldBeFalse();
    }

    [Fact]
    public void ResolveExecutablePath_on_windows_tries_known_extensions()
    {
        using var sandbox = new DirectorySandbox();
        var executable = sandbox.CreateFile("claude.cmd");
        var resolver = new CliExecutableResolver(static () => true);

        var resolved = resolver.ResolveExecutablePath("claude", sandbox.BuildEnvironment(pathExt: ".EXE;.CMD;.BAT"));

        resolved.ShouldBe(executable);
    }

    private sealed class DirectorySandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-resolver-{Guid.NewGuid():N}");

        public DirectorySandbox()
        {
            Directory.CreateDirectory(_root);
        }

        public string CreateFile(string relativePath)
        {
            var fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Empty);
            return fullPath;
        }

        public IReadOnlyDictionary<string, string?> BuildEnvironment(string? pathExt = null)
        {
            return new Dictionary<string, string?>
            {
                ["PATH"] = _root,
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
