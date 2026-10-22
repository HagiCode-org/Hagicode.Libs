using System.Diagnostics;
using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class WindowsCommandScriptLaunchTests
{
    private const string ScriptPath = @"C:\tools\claude.cmd";
    private const string SystemDirectory = @"C:\Windows\System32";

    [TestCase("", "\"\"")]
    [TestCase("plain_token-1", "plain_token-1")]
    [TestCase(@"C:\dir\", @"C:\dir\")]
    [TestCase(@"C:\my dir\", @"""C:\my dir\\""")]
    [TestCase(@"C:\Program Files\claude.cmd", @"""C:\Program Files\claude.cmd""")]
    [TestCase("{\"mode\":\"fast\"}", "\"{\\\"mode\\\":\\\"fast\\\"}\"")]
    [TestCase("a\\\"b\"", "\"a\\\\\\\"b\\\"\"")]
    public void EscapeArgument_quotes_and_escapes_each_value_for_the_c_runtime(string value, string expected)
    {
        WindowsCommandScriptLaunch.EscapeArgument(value, argumentIndex: 0).ShouldBe(expected);
    }

    [TestCase("a&b", "\"a&b\"")]
    [TestCase("caret^x", "\"caret^x\"")]
    [TestCase("pipe|x", "\"pipe|x\"")]
    [TestCase("paren(x)", "\"paren(x)\"")]
    [TestCase("{\"mode\":\"fast\"}", "\"{\\\"mode\\\":\\\"fast\\\"}\"")]
    public void Create_accepts_metacharacters_that_stay_inside_quotes(string value, string escaped)
    {
        var start = WindowsCommandScriptLaunch.Create(ScriptPath, [value], SystemDirectory);

        start.Arguments.ShouldBe($"/d /v:off /s /c \"\"{ScriptPath}\" {escaped}\"");
    }

    [TestCase("x\" & echo injected & \"y", 0, "shell metacharacter")]
    [TestCase("100%", 0, "cmd.exe expands")]
    [TestCase("line1\nline2", 0, "NUL, carriage return or line feed")]
    [TestCase("line1\rline2", 0, "NUL, carriage return or line feed")]
    [TestCase("say \"hi", 0, "odd number of double quotes")]
    public void Create_rejects_arguments_that_cmd_exe_could_read_as_commands(string value, int index, string ruleFragment)
    {
        var exception = Should.Throw<CliCommandScriptArgumentException>(() =>
            WindowsCommandScriptLaunch.Create(ScriptPath, ["ok", value], SystemDirectory));

        exception.ArgumentIndex.ShouldBe(index + 1);
        exception.Rule.ShouldContain(ruleFragment);
    }

    [Test]
    public void Create_rejects_a_script_path_that_holds_a_quote()
    {
        var exception = Should.Throw<CliCommandScriptArgumentException>(() =>
            WindowsCommandScriptLaunch.Create("C:\\tools\\bad\"name.cmd", [], SystemDirectory));

        exception.ArgumentIndex.ShouldBeNull();
    }

    [Test]
    public void Create_error_tells_the_caller_to_use_the_native_exe_and_never_prints_the_value()
    {
        var exception = Should.Throw<CliCommandScriptArgumentException>(() =>
            WindowsCommandScriptLaunch.Create(ScriptPath, ["x\" & echo SECRETVALUE & \"y"], SystemDirectory));

        exception.Message.ShouldContain("native .exe");
        exception.Message.ShouldContain("another way");
        exception.Message.ShouldNotContain("SECRETVALUE");
    }

    [Test]
    public void Create_builds_the_documented_argument_string_for_spaced_arguments()
    {
        var start = WindowsCommandScriptLaunch.Create(ScriptPath, ["--append-system-prompt", "reply in Chinese"], SystemDirectory);

        start.Arguments.ShouldBe("/d /v:off /s /c \"\"C:\\tools\\claude.cmd\" --append-system-prompt \"reply in Chinese\"\"");
    }

    [Test]
    public void Create_uses_the_cmd_exe_in_the_system_directory_even_with_a_comspec_override_or_a_working_directory_cmd()
    {
        using var sandbox = new TempDirectory();
        var workingDirectoryCmd = Path.Combine(sandbox.Path, "cmd.exe");
        File.WriteAllText(workingDirectoryCmd, string.Empty);
        var originalComSpec = System.Environment.GetEnvironmentVariable("ComSpec");
        var originalDirectory = Directory.GetCurrentDirectory();

        System.Environment.SetEnvironmentVariable("ComSpec", Path.Combine(sandbox.Path, "evil.exe"));
        Directory.SetCurrentDirectory(sandbox.Path);
        try
        {
            var start = WindowsCommandScriptLaunch.Create(ScriptPath, [], SystemDirectory);

            start.FileName.ShouldBe(Path.Combine(SystemDirectory, "cmd.exe"));
            start.FileName.ShouldNotBe(workingDirectoryCmd);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            System.Environment.SetEnvironmentVariable("ComSpec", originalComSpec);
        }
    }

    [Test]
    public void Create_without_a_system_directory_argument_uses_the_environment_system_directory()
    {
        var start = WindowsCommandScriptLaunch.Create(ScriptPath, []);

        start.FileName.ShouldBe(Path.Combine(System.Environment.SystemDirectory, "cmd.exe"));
    }

    [Test]
    public void ConfigureExecutable_on_windows_runs_a_command_script_through_arguments_and_not_argument_list()
    {
        var startInfo = new ProcessStartInfo();

        WindowsCommandScriptLaunch.ConfigureExecutable(startInfo, ScriptPath, ["--a", "b c"], isWindows: true);

        startInfo.FileName.ShouldBe(Path.Combine(System.Environment.SystemDirectory, "cmd.exe"));
        startInfo.Arguments.ShouldBe("/d /v:off /s /c \"\"C:\\tools\\claude.cmd\" --a \"b c\"\"");
        startInfo.ArgumentList.ShouldBeEmpty();
    }

    [Test]
    public void ConfigureExecutable_keeps_an_exe_as_file_name_with_argument_list()
    {
        var startInfo = new ProcessStartInfo();

        WindowsCommandScriptLaunch.ConfigureExecutable(startInfo, @"C:\Program Files\Anthropic\claude.exe", ["a & echo injected"], isWindows: true);

        startInfo.FileName.ShouldBe(@"C:\Program Files\Anthropic\claude.exe");
        startInfo.ArgumentList.ShouldBe(["a & echo injected"]);
        startInfo.Arguments.ShouldBeEmpty();
    }

    [Test]
    public void ConfigureExecutable_on_non_windows_never_wraps_a_command_script()
    {
        var startInfo = new ProcessStartInfo();

        WindowsCommandScriptLaunch.ConfigureExecutable(startInfo, "/opt/anthropic/claude.cmd", ["--a"], isWindows: false);

        startInfo.FileName.ShouldBe("/opt/anthropic/claude.cmd");
        startInfo.ArgumentList.ShouldBe(["--a"]);
    }

    [TestCase("claude.cmd", true)]
    [TestCase("CLAUDE.BAT", true)]
    [TestCase("claude.exe", false)]
    [TestCase("claude", false)]
    public void IsCommandScript_recognizes_only_cmd_and_bat(string path, bool expected)
    {
        WindowsCommandScriptLaunch.IsCommandScript(path).ShouldBe(expected);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"command-script-launch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
