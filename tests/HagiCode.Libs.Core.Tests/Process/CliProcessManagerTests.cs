using Shouldly;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CliProcessManagerTests
{
    private readonly CliProcessManager _manager = new();

    [Fact]
    public async Task ExecuteAsync_captures_standard_output_and_error()
    {
        var result = await _manager.ExecuteAsync(CreateShellContext("printf 'hello'; printf 'oops' >&2"));

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldBe("hello");
        result.StandardError.ShouldBe("oops");
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

        result.ExitCode.ShouldBe(-1);
        result.StandardError.ShouldContain("timed out");
    }

    [Fact]
    public void CreateStartInfo_redirects_streams_and_preserves_utf8()
    {
        var startInfo = _manager.CreateStartInfo(CreateShellContext("printf 'ok'"));

        startInfo.RedirectStandardInput.ShouldBeTrue();
        startInfo.RedirectStandardOutput.ShouldBeTrue();
        startInfo.RedirectStandardError.ShouldBeTrue();
        startInfo.StandardOutputEncoding.ShouldNotBeNull();
    }

    private static ProcessStartContext CreateShellContext(string command)
    {
        return new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", command]
        };
    }
}
