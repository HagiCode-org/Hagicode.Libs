using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Core.Tests.Transport;

public sealed class SubprocessTransportTests
{
    [Fact]
    public async Task SubprocessTransport_sends_and_receives_json_lines()
    {
        var manager = new CliProcessManager();
        await using var transport = new SubprocessTransport(manager, new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments =
            [
                "-lc",
                "while IFS= read -r line; do printf '%s\\n' \"$line\"; printf '%s' \"$line\" | grep -q '\"type\":\"result\"' && break; done"
            ]
        });

        await transport.ConnectAsync();
        await transport.SendAsync(new CliMessage("assistant", JsonSerializer.SerializeToElement(new { text = "hello" })));
        await transport.SendAsync(new CliMessage("result", JsonSerializer.SerializeToElement(new { done = true })));

        var messages = new List<CliMessage>();
        await foreach (var message in transport.ReceiveAsync())
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["assistant", "result"]);
    }

    [Fact]
    public async Task SubprocessTransport_tolerates_utf8_bom_prefix_on_first_output_line()
    {
        var manager = new CliProcessManager();
        await using var transport = new SubprocessTransport(manager, new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments =
            [
                "-lc",
                "printf '\\357\\273\\277{\"type\":\"assistant\",\"text\":\"hello\"}\\n'; printf '{\"type\":\"result\",\"done\":true}\\n'"
            ]
        });

        await transport.ConnectAsync();

        var messages = new List<CliMessage>();
        await foreach (var message in transport.ReceiveAsync())
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["assistant", "result"]);
        messages[0].Content.GetProperty("text").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task SubprocessTransport_rejects_send_before_connect()
    {
        var manager = new CliProcessManager();
        await using var transport = new SubprocessTransport(manager, new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", "cat"]
        });

        var action = async () => await transport.SendAsync(new CliMessage("user", JsonSerializer.SerializeToElement(new { text = "oops" })));

        await Should.ThrowAsync<InvalidOperationException>(action());
    }

    [Fact]
    public async Task SubprocessTransport_windows_batch_shim_under_whitespace_path_preserves_messages()
    {
        var manager = new WindowsBatchRecordingCliProcessManager(
            @"C:\Program Files\Anthropic\claude.cmd",
            "while IFS= read -r line; do printf '%s\\n' \"$line\"; printf '%s' \"$line\" | grep -q '\"type\":\"result\"' && break; done");
        await using var transport = new SubprocessTransport(manager, new ProcessStartContext
        {
            ExecutablePath = "claude",
            Arguments = ["--output-format", "stream-json", "--append-system-prompt", "reply in Chinese"]
        });

        await transport.ConnectAsync();
        await transport.SendAsync(new CliMessage("assistant", JsonSerializer.SerializeToElement(new { text = "hello" })));
        await transport.SendAsync(new CliMessage("result", JsonSerializer.SerializeToElement(new { done = true })));

        var messages = new List<CliMessage>();
        await foreach (var message in transport.ReceiveAsync())
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["assistant", "result"]);
        manager.LastStartInfo.ShouldNotBeNull();
        manager.LastStartInfo.FileName.ShouldBe("cmd.exe");
        manager.LastStartInfo.ArgumentList.ShouldBe(
        [
            "/d",
            "/s",
            "/c",
            """
            ""C:\Program Files\Anthropic\claude.cmd" --output-format stream-json --append-system-prompt "reply in Chinese""
            """
        ]);
        manager.LastStartInfo.StandardErrorEncoding.ShouldNotBeNull();
        manager.LastStartInfo.StandardErrorEncoding.WebName.ShouldBe(Encoding.Unicode.WebName);
    }

    [Fact]
    public async Task SubprocessTransport_preserves_non_zero_exit_diagnostics_for_unrelated_failures()
    {
        var manager = new CliProcessManager();
        await using var transport = new SubprocessTransport(manager, new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", "printf 'startup failed' >&2; exit 23"]
        });

        await transport.ConnectAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in transport.ReceiveAsync())
            {
            }
        });

        exception.Message.ShouldContain("code 23");
        exception.Message.ShouldContain("startup failed");
    }

    private sealed class WindowsBatchRecordingCliProcessManager(string resolvedExecutablePath, string shellCommand) : CliProcessManager
    {
        public ProcessStartInfo? LastStartInfo { get; private set; }

        protected override bool IsWindows() => true;

        protected override string ResolveWindowsCommandInterpreterPath() => "cmd.exe";

        protected override string ResolveExecutablePath(string executablePath, IReadOnlyDictionary<string, string?>? environmentVariables)
            => resolvedExecutablePath;

        protected override Encoding ResolveWindowsBatchStandardErrorEncoding(Encoding fallbackEncoding)
            => Encoding.Unicode;

        public override ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            LastStartInfo = CreateStartInfo(context);

            var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-lc");
            process.StartInfo.ArgumentList.Add(shellCommand);

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the transport test subprocess.");
            }

            return ValueTask.FromResult(new CliProcessHandle(
                process,
                process.StandardInput,
                process.StandardOutput,
                process.StandardError));
        }
    }
}
