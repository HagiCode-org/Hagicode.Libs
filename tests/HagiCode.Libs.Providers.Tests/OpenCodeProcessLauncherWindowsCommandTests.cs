using System.Diagnostics;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class OpenCodeProcessLauncherWindowsCommandTests
{
    [Test]
    public void CreateStartInfo_on_windows_starts_a_resolved_cmd_through_cmd_exe_and_not_as_file_name()
    {
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver(), static () => true);

        var startInfo = launcher.CreateStartInfo(
            @"C:\tools\opencode.cmd",
            new OpenCodeStandaloneServerOptions(),
            new Dictionary<string, string?>(),
            port: 4096);

        startInfo.FileName.ShouldBe(Path.Combine(System.Environment.SystemDirectory, "cmd.exe"));
        startInfo.Arguments.ShouldBe("/d /v:off /s /c \"\"C:\\tools\\opencode.cmd\" serve \"--hostname=127.0.0.1\" \"--port=4096\"\"");
        startInfo.ArgumentList.ShouldBeEmpty();
    }

    [Test]
    public void CreateStartInfo_on_windows_keeps_an_exe_with_file_name_and_argument_list()
    {
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver(), static () => true);

        var startInfo = launcher.CreateStartInfo(
            @"C:\tools\opencode.exe",
            new OpenCodeStandaloneServerOptions(),
            new Dictionary<string, string?>(),
            port: 4096);

        startInfo.FileName.ShouldBe(@"C:\tools\opencode.exe");
        startInfo.ArgumentList.ShouldBe(["serve", "--hostname=127.0.0.1", "--port=4096"]);
        startInfo.Arguments.ShouldBeEmpty();
    }
}
