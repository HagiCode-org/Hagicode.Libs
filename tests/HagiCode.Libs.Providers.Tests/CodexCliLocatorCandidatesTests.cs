using ManagedCode.CodexSharpSDK.Internal;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexCliLocatorCandidatesTests
{
    [Test]
    public void Windows_path_fallback_holds_only_the_native_codex_exe()
    {
        CodexCliLocator.GetPathExecutableCandidates(isWindows: true).ShouldBe(["codex.exe"]);
    }

    [Test]
    public void Unix_path_fallback_is_unchanged()
    {
        CodexCliLocator.GetPathExecutableCandidates(isWindows: false).ShouldBe(["codex"]);
    }

    [Test]
    public void Windows_path_lookup_ignores_codex_cmd_and_codex_bat()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "codex.cmd"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "codex.bat"), string.Empty);

            var found = CodexCliLocator.TryResolvePathExecutable(directory, isWindows: true, out var executablePath);

            found.ShouldBeFalse();
            executablePath.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
