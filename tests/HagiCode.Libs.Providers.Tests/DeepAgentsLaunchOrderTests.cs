using System.Reflection;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.DeepAgents;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DeepAgentsLaunchOrderTests
{
    [Test]
    public void Uvx_candidates_list_the_native_exe_before_the_cmd_shim()
    {
        var candidates = (string[])typeof(DeepAgentsLaunchResolver)
            .GetField("UvxExecutableCandidates", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        candidates.ShouldBe(["uvx", "uvx.exe", "uvx.cmd", "uvx.bat"]);
    }

    [Test]
    public void Uvx_launch_on_windows_uses_uvx_exe_when_a_cmd_shim_sits_beside_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"deepagents-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "uvx.cmd"), string.Empty);
            var exe = Path.Combine(directory, "uvx.exe");
            File.WriteAllText(exe, string.Empty);
            var resolver = new DeepAgentsLaunchResolver(new CliExecutableResolver(static () => true, static () => System.Runtime.InteropServices.Architecture.X64));

            var launcher = resolver.Resolve(
                new DeepAgentsOptions(),
                [],
                new Dictionary<string, string?> { ["PATH"] = directory, ["PATHEXT"] = ".CMD;.EXE;.BAT" });

            launcher.ShouldNotBeNull();
            launcher.ExecutablePath.ShouldBe(Path.GetFullPath(exe));
            launcher.UsesFallbackLauncher.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
