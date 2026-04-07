using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codex;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexExecTransportTests
{
    [Fact]
    public async Task SendAsync_writes_chinese_prompt_as_utf8_bytes_by_default()
    {
        const string chinesePrompt = "你好，Codex";
        var processManager = new RecordingCliProcessManager();
        await using var transport = new CodexExecTransport(processManager, new ProcessStartContext
        {
            ExecutablePath = "codex",
            Arguments = ["exec", "--experimental-json"]
        });

        await transport.ConnectAsync();
        await transport.SendAsync(new CliMessage("input", JsonSerializer.SerializeToElement(new
        {
            input = chinesePrompt
        })));

        processManager.LastStartContext.ShouldNotBeNull();
        processManager.LastStartContext.InputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        processManager.GetWrittenBytes().ShouldBe(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(chinesePrompt)).ToArray());
    }

    [Fact]
    public async Task SendAsync_respects_explicit_input_encoding_override()
    {
        const string chinesePrompt = "中文续聊";
        var processManager = new RecordingCliProcessManager();
        var expectedEncoding = Encoding.Unicode;
        await using var transport = new CodexExecTransport(processManager, new ProcessStartContext
        {
            ExecutablePath = "codex",
            InputEncoding = expectedEncoding
        });

        await transport.ConnectAsync();
        await transport.SendAsync(new CliMessage("input", JsonSerializer.SerializeToElement(new
        {
            input = chinesePrompt
        })));

        processManager.LastStartContext.ShouldNotBeNull();
        processManager.LastStartContext.InputEncoding.WebName.ShouldBe(expectedEncoding.WebName);
        processManager.GetWrittenBytes().ShouldBe(expectedEncoding.GetPreamble().Concat(expectedEncoding.GetBytes(chinesePrompt)).ToArray());
    }

    private sealed class RecordingCliProcessManager : CliProcessManager
    {
        private readonly MemoryStream _stdin = new();

        public ProcessStartContext? LastStartContext { get; private set; }

        public byte[] GetWrittenBytes() => _stdin.ToArray();

        public override ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            LastStartContext = context;
            _stdin.SetLength(0);
            _stdin.Position = 0;

            var standardInput = new StreamWriter(
                _stdin,
                context.InputEncoding,
                bufferSize: 1024,
                leaveOpen: true);
            var standardOutput = new StreamReader(new MemoryStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
            var standardError = new StreamReader(new MemoryStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);

            return ValueTask.FromResult(new CliProcessHandle(new Process(), standardInput, standardOutput, standardError));
        }

        public override async Task StopAsync(CliProcessHandle? handle, CancellationToken cancellationToken = default)
        {
            if (handle is null)
            {
                return;
            }

            await handle.DisposeAsync();
        }
    }
}
