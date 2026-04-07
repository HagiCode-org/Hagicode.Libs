using System.Text;
using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CliProcessManagerTests
{
    private readonly CliProcessManager _manager = new();

    [Fact]
    public async Task ExecuteAsync_captures_standard_output_error_and_chunks()
    {
        var result = await _manager.ExecuteAsync(CreateShellContext("printf 'hello'; printf 'oops' >&2"));

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldBe("hello");
        result.StandardError.ShouldBe("oops");
        result.CapturedOutput.ShouldContain(chunk => chunk.Channel == ProcessOutputChannel.StandardOutput && chunk.Text.Contains("hello", StringComparison.Ordinal));
        result.CapturedOutput.ShouldContain(chunk => chunk.Channel == ProcessOutputChannel.StandardError && chunk.Text.Contains("oops", StringComparison.Ordinal));
        result.CommandPreview.ShouldContain("/bin/sh");
        result.StartedAtUtc.ShouldNotBeNull();
        result.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_applies_environment_variable_overrides()
    {
        var result = await _manager.ExecuteAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", "printf '%s' \"$TEST_VALUE\""],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["TEST_VALUE"] = "from-child"
            }
        });

        result.StandardOutput.ShouldBe("from-child");
    }

    [Fact]
    public async Task ExecuteAsync_returns_timeout_result_when_process_runs_too_long()
    {
        var result = await _manager.ExecuteAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", "sleep 2"],
            Timeout = TimeSpan.FromMilliseconds(100)
        });

        result.ExitCode.ShouldNotBe(0);
        result.TimedOut.ShouldBeTrue();
        result.StandardError.ShouldContain("timed out");
    }

    [Fact]
    public void CreateStartInfo_redirects_streams_and_preserves_utf8()
    {
        var startInfo = _manager.CreateStartInfo(CreateShellContext("printf 'ok'"));

        startInfo.RedirectStandardInput.ShouldBeTrue();
        startInfo.RedirectStandardOutput.ShouldBeTrue();
        startInfo.RedirectStandardError.ShouldBeTrue();
        startInfo.StandardInputEncoding.ShouldNotBeNull();
        startInfo.StandardInputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardOutputEncoding.ShouldNotBeNull();
        startInfo.StandardOutputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardErrorEncoding.ShouldNotBeNull();
        startInfo.StandardErrorEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
    }

    [Fact]
    public void CreateStartInfo_on_windows_wraps_resolved_batch_files_with_cmd()
    {
        var manager = new TestCliProcessManager(@"C:\tools\npm.cmd");
        var startInfo = manager.CreateStartInfo(new ProcessStartContext
        {
            ExecutablePath = "npm",
            Arguments = ["install", "--global", "@openai/codex"]
        });

        startInfo.FileName.ShouldBe("cmd.exe");
        startInfo.ArgumentList.ShouldBe(["/c", @"C:\tools\npm.cmd", "install", "--global", "@openai/codex"]);
        startInfo.StandardInputEncoding.ShouldNotBeNull();
        startInfo.StandardInputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
    }

    private static ProcessStartContext CreateShellContext(string command)
    {
        return new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", command]
        };
    }

    private sealed class TestCliProcessManager(string resolvedExecutablePath) : CliProcessManager
    {
        protected override bool IsWindows() => true;

        protected override string ResolveExecutablePath(string executablePath, IReadOnlyDictionary<string, string?>? environmentVariables)
            => resolvedExecutablePath;
    }
}
