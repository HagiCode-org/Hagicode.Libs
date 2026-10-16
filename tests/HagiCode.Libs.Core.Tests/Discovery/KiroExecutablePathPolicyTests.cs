using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class KiroExecutablePathPolicyTests
{
    [Test]
    public void IsAcceptedPath_accepts_kiro_cli_and_its_windows_shim()
    {
        KiroExecutablePathPolicy.IsAcceptedPath("/usr/local/bin/kiro-cli").ShouldBeTrue();
        KiroExecutablePathPolicy.IsAcceptedPath(@"C:\tools\kiro-cli.cmd").ShouldBeTrue();
    }

    [Test]
    public void IsAcceptedPath_rejects_a_bare_kiro_executable()
    {
        KiroExecutablePathPolicy.IsAcceptedPath("/usr/local/bin/kiro").ShouldBeFalse();
    }

    [Test]
    public void IsAcceptedPath_rejects_blank_paths()
    {
        KiroExecutablePathPolicy.IsAcceptedPath(null).ShouldBeFalse();
        KiroExecutablePathPolicy.IsAcceptedPath("  ").ShouldBeFalse();
    }

    [Test]
    public void SelectAcceptedPaths_keeps_only_kiro_cli_names_in_order()
    {
        var accepted = KiroExecutablePathPolicy.SelectAcceptedPaths(
        [
            "/a/kiro",
            "/b/kiro-cli",
            "/c/unrelated-cli",
            "/d/kiro-cli.cmd"
        ]);

        accepted.ShouldBe(["/b/kiro-cli", "/d/kiro-cli.cmd"]);
    }
}
