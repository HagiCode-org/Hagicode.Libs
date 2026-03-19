using System.Runtime.CompilerServices;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Core.Tests.Transport;

public sealed class CliTransportContractTests
{
    [Fact]
    public async Task Transport_contract_can_send_receive_and_disconnect()
    {
        await using var transport = new InMemoryCliTransport();
        await transport.ConnectAsync();

        var payload = JsonSerializer.SerializeToElement(new { type = "assistant", text = "hello" });
        await transport.SendAsync(new CliMessage("assistant", payload));

        var messages = new List<CliMessage>();
        await foreach (var message in transport.ReceiveAsync())
        {
            messages.Add(message);
        }

        transport.IsConnected.ShouldBeTrue();
        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("hello");

        await transport.DisconnectAsync();
        transport.IsConnected.ShouldBeFalse();
    }

    private sealed class InMemoryCliTransport : ICliTransport
    {
        private readonly Queue<CliMessage> _messages = new();

        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (_messages.TryDequeue(out var message))
            {
                yield return message;
                await Task.Yield();
            }
        }

        public Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException();
            }

            _messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
