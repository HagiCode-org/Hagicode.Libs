using System.Diagnostics;
using ManagedCode.CodexSharpSDK.Execution;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexExecWindowsCommandTests
{
    [Test]
    public void CreateStartInfo_on_windows_runs_a_codex_command_script_through_cmd_exe()
    {
        var invocation = new CodexProcessInvocation(@"C:\tools\codex.cmd", ["exec", "--json"], new Dictionary<string, string>(), string.Empty);

        var startInfo = DefaultCodexProcessRunner.CreateStartInfo(invocation, isWindows: true);

        startInfo.FileName.ShouldBe(Path.Combine(System.Environment.SystemDirectory, "cmd.exe"));
        startInfo.Arguments.ShouldBe("/d /v:off /s /c \"\"C:\\tools\\codex.cmd\" exec --json\"");
        startInfo.ArgumentList.ShouldBeEmpty();
    }

    [Test]
    public void CreateStartInfo_on_windows_keeps_a_native_codex_exe_with_file_name_and_argument_list()
    {
        var invocation = new CodexProcessInvocation(@"C:\tools\codex.exe", ["exec", "--json"], new Dictionary<string, string>(), string.Empty);

        var startInfo = DefaultCodexProcessRunner.CreateStartInfo(invocation, isWindows: true);

        startInfo.FileName.ShouldBe(@"C:\tools\codex.exe");
        startInfo.ArgumentList.ShouldBe(["exec", "--json"]);
        startInfo.Arguments.ShouldBeEmpty();
    }
}
