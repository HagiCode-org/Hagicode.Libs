using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Process;

// These tests start a real .cmd on a Windows host. Off Windows they are skipped, and they still appear in dotnet test --list-tests.
[Category("Integration")]
[Category("WindowsOnly")]
public sealed class WindowsCommandScriptRealLaunchTests
{
    [Test]
    public async Task Command_script_under_a_spaced_path_receives_spaced_json_and_ampersand_arguments_as_data()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows command-script launch test.");
        }

        using var fixture = new CommandScriptFixture();
        var manager = new CliProcessManager();

        var result = await manager.ExecuteAsync(new ProcessStartContext
        {
            ExecutablePath = fixture.ScriptPath,
            Arguments = ["--append-system-prompt", "reply in Chinese", "{\"mode\":\"fast\"}", "a&b"]
        });

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("--append-system-prompt");
        result.StandardOutput.ShouldContain("\"reply in Chinese\"");
        result.StandardOutput.ShouldContain("a&b");
        result.ResolvedExecutablePath.ShouldBe(fixture.ScriptPath);
    }

    [Test]
    public async Task Injection_attempt_is_refused_before_start_and_the_marker_file_is_never_created()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows command-script launch test.");
        }

        using var fixture = new CommandScriptFixture();
        var manager = new CliProcessManager();
        var marker = Path.Combine(fixture.Directory, "injected.txt");

        await Should.ThrowAsync<CliCommandScriptArgumentException>(() => manager.ExecuteAsync(new ProcessStartContext
        {
            ExecutablePath = fixture.ScriptPath,
            Arguments = [$"x\" & echo injected > \"{marker}\" & \"y"]
        }));

        File.Exists(marker).ShouldBeFalse();
    }

    private sealed class CommandScriptFixture : IDisposable
    {
        public CommandScriptFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"cmd fixture {Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            ScriptPath = Path.Combine(Directory, "echo-args.cmd");
            File.WriteAllText(ScriptPath, "@echo off\r\necho %*\r\n");
        }

        public string Directory { get; }

        public string ScriptPath { get; }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
