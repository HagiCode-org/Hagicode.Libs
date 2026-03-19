using System.Text.Json;
using FluentAssertions;
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

        messages.Select(static message => message.Type).Should().Equal("assistant", "result");
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

        await action.Should().ThrowAsync<InvalidOperationException>();
    }
}
