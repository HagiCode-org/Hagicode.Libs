using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using Microsoft.Extensions.Options;
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
    public void CreateStartInfo_on_windows_uses_resolved_batch_files_directly_after_resolution()
    {
        var manager = new TestCliProcessManager(@"C:\tools\npm.cmd");
        var startInfo = manager.CreateStartInfo(new ProcessStartContext
        {
            ExecutablePath = "npm",
            Arguments = ["install", "--global", "@openai/codex"]
        });

        startInfo.FileName.ShouldBe(@"C:\tools\npm.cmd");
        startInfo.ArgumentList.ShouldBe(["install", "--global", "@openai/codex"]);
        startInfo.StandardInputEncoding.ShouldNotBeNull();
        startInfo.StandardInputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardErrorEncoding.ShouldNotBeNull();
        startInfo.StandardErrorEncoding.WebName.ShouldBe(Encoding.Unicode.WebName);
    }

    [Fact]
    public void CreateStartInfo_on_windows_preserves_batch_paths_with_spaces_and_argument_order()
    {
        var manager = new TestCliProcessManager(@"C:\Program Files\Anthropic\claude.cmd");
        var startInfo = manager.CreateStartInfo(new ProcessStartContext
        {
            ExecutablePath = "claude",
            Arguments = ["--output-format", "stream-json", "--append-system-prompt", "reply in Chinese"]
        });

        startInfo.FileName.ShouldBe(@"C:\Program Files\Anthropic\claude.cmd");
        startInfo.ArgumentList.ShouldBe(["--output-format", "stream-json", "--append-system-prompt", "reply in Chinese"]);
        startInfo.StandardErrorEncoding.ShouldNotBeNull();
        startInfo.StandardErrorEncoding.WebName.ShouldBe(Encoding.Unicode.WebName);
    }

    [Fact]
    public void CreateStartInfo_on_windows_keeps_direct_executables_outside_cmd_shim_branch()
    {
        var manager = new TestCliProcessManager(@"C:\Program Files\Anthropic\claude.exe");
        var startInfo = manager.CreateStartInfo(new ProcessStartContext
        {
            ExecutablePath = "claude",
            Arguments = ["--output-format", "stream-json"]
        });

        startInfo.FileName.ShouldBe(@"C:\Program Files\Anthropic\claude.exe");
        startInfo.ArgumentList.ShouldBe(["--output-format", "stream-json"]);
    }

    [Fact]
    public void CreateStartInfo_on_non_windows_does_not_wrap_batch_extensions()
    {
        var manager = new TestCliProcessManager("/opt/anthropic/claude.cmd", isWindows: false);
        var startInfo = manager.CreateStartInfo(new ProcessStartContext
        {
            ExecutablePath = "claude",
            Arguments = ["--output-format", "stream-json"]
        });

        startInfo.FileName.ShouldBe("/opt/anthropic/claude.cmd");
        startInfo.ArgumentList.ShouldBe(["--output-format", "stream-json"]);
    }

    [Fact]
    public async Task StartAsync_persists_owned_process_and_stop_async_removes_record()
    {
        using var tempDirectory = new TempDirectory();
        var statePath = Path.Combine(tempDirectory.Path, "cli-owned-processes.json");
        var manager = CreateOwnershipManager(statePath);

        var handle = await manager.StartAsync(CreateOwnershipShellContext("sleep 30", "codex"));
        var stopped = false;

        try
        {
            var states = await new CliOwnedProcessRegistry().ReadAsync(statePath);
            states.Count.ShouldBe(1);
            states[0].ProviderName.ShouldBe("codex");
            states[0].Pid.ShouldBe(handle.Process.Id);

            await manager.StopAsync(handle);
            stopped = true;

            File.Exists(statePath).ShouldBeFalse();
        }
        finally
        {
            if (!stopped)
            {
                await manager.StopAsync(handle);
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_removes_owned_process_record_after_process_exit()
    {
        using var tempDirectory = new TempDirectory();
        var statePath = Path.Combine(tempDirectory.Path, "cli-owned-processes.json");
        var manager = CreateOwnershipManager(statePath);

        var handle = await manager.StartAsync(CreateOwnershipShellContext("true", "claude-code"));

        await handle.Process.WaitForExitAsync();
        File.Exists(statePath).ShouldBeTrue();

        await handle.DisposeAsync();

        File.Exists(statePath).ShouldBeFalse();
    }

    [Fact]
    public async Task RecoverOwnedProcessesAsync_deletes_corrupted_state_file()
    {
        using var tempDirectory = new TempDirectory();
        var statePath = Path.Combine(tempDirectory.Path, "cli-owned-processes.json");
        await File.WriteAllTextAsync(statePath, "{not-json");
        var manager = CreateOwnershipManager(statePath);

        var recovered = await manager.RecoverOwnedProcessesAsync();

        recovered.ShouldBe(0);
        File.Exists(statePath).ShouldBeFalse();
    }

    [Fact]
    public async Task RecoverOwnedProcessesAsync_skips_start_time_mismatch_and_removes_record()
    {
        using var tempDirectory = new TempDirectory();
        var statePath = Path.Combine(tempDirectory.Path, "cli-owned-processes.json");
        var manager = CreateOwnershipManager(statePath);
        var registry = new CliOwnedProcessRegistry();
        var handle = await manager.StartAsync(CreateOwnershipShellContext("sleep 30", "kimi"));

        try
        {
            var state = (await registry.ReadAsync(statePath)).Single();
            var mismatchedState = state with { StartedAtUtc = state.StartedAtUtc.AddSeconds(-5) };
            await WriteOwnedProcessStatesAsync(statePath, mismatchedState);

            var recovered = await manager.RecoverOwnedProcessesAsync();

            recovered.ShouldBe(0);
            handle.Process.HasExited.ShouldBeFalse();
            File.Exists(statePath).ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    [Fact]
    public async Task RecoverOwnedProcessesAsync_kills_matching_owned_process_and_removes_record()
    {
        using var tempDirectory = new TempDirectory();
        var statePath = Path.Combine(tempDirectory.Path, "cli-owned-processes.json");
        var manager = CreateOwnershipManager(statePath);
        var handle = await manager.StartAsync(CreateOwnershipShellContext("sleep 30", "hermes"));

        try
        {
            var recovered = await manager.RecoverOwnedProcessesAsync();

            recovered.ShouldBe(1);
            await handle.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            handle.Process.HasExited.ShouldBeTrue();
            File.Exists(statePath).ShouldBeFalse();
        }
        finally
        {
            await manager.StopAsync(handle);
        }
    }

    private static ProcessStartContext CreateShellContext(string command)
    {
        return new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", command]
        };
    }

    private static ProcessStartContext CreateOwnershipShellContext(string command, string providerName)
    {
        return new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", command],
            Ownership = new CliProcessOwnershipRegistration { ProviderName = providerName }
        };
    }

    private static CliProcessManager CreateOwnershipManager(string statePath)
    {
        return new CliProcessManager(
            new CliExecutableResolver(),
            Options.Create(new CliProcessOwnershipOptions
            {
                Enabled = true,
                StateFilePath = statePath
            }),
            new CliOwnedProcessRegistry());
    }

    private static async Task WriteOwnedProcessStatesAsync(string statePath, params CliOwnedProcessState[] states)
    {
        var directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            statePath,
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["processes"] = states
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private sealed class TestCliProcessManager(string resolvedExecutablePath, bool isWindows = true) : CliProcessManager
    {
        protected override bool IsWindows() => isWindows;

        protected override string ResolveExecutablePath(string executablePath, IReadOnlyDictionary<string, string?>? environmentVariables)
            => resolvedExecutablePath;

        protected override Encoding ResolveWindowsBatchStandardErrorEncoding(Encoding fallbackEncoding)
            => Encoding.Unicode;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cli-process-manager-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
