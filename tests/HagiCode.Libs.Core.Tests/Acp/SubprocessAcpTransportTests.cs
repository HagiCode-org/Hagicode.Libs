using System.Text;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Acp;

public sealed class SubprocessAcpTransportTests
{
    [Fact]
    public async Task DisconnectAsync_ignores_expected_operation_canceled_errors_from_standard_error_pump()
    {
        var manager = new ThrowingStandardErrorCliProcessManager(
            () => new IOException("Operation canceled"));
        await using var transport = new SubprocessAcpTransport(manager, new ProcessStartContext
        {
            ExecutablePath = "reasonix"
        });

        await transport.ConnectAsync();

        await transport.DisconnectAsync();
    }

    [Fact]
    public async Task DisconnectAsync_preserves_unrelated_standard_error_pump_failures()
    {
        var manager = new ThrowingStandardErrorCliProcessManager(
            () => new IOException("stderr pipe failed unexpectedly"));
        await using var transport = new SubprocessAcpTransport(manager, new ProcessStartContext
        {
            ExecutablePath = "reasonix"
        });

        await transport.ConnectAsync();

        var exception = await Should.ThrowAsync<IOException>(async () => await transport.DisconnectAsync());

        exception.Message.ShouldContain("stderr pipe failed unexpectedly");
    }

    private sealed class ThrowingStandardErrorCliProcessManager(Func<Exception> createReadException) : CliProcessManager
    {
        public override ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var standardInput = new StreamWriter(Stream.Null, new UTF8Encoding(false));
            var standardOutput = new StreamReader(new MemoryStream());
            var standardError = new ThrowingStreamReader(createReadException);

            return ValueTask.FromResult(new CliProcessHandle(process, standardInput, standardOutput, standardError));
        }

        public override Task StopAsync(CliProcessHandle? handle, CancellationToken cancellationToken = default)
        {
            return handle?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        }
    }

    private sealed class ThrowingStreamReader(Func<Exception> createException) : StreamReader(Stream.Null)
    {
        public override Task<string?> ReadLineAsync()
        {
            return Task.FromException<string?>(createException());
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromException<string?>(createException());
        }
    }
}
