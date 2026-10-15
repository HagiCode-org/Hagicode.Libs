using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using HagiCode.Libs.Core.Acp;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Acp;

public sealed class WebSocketAcpTransportTests
{
    [Test]
    public async Task ReceiveMessagesAsync_cancellation_between_messages_throws()
    {
        var port = GetFreeLoopbackPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = ServeTwoFramesAsync(listener, serverCancellation.Token);

        await using var transport = new WebSocketAcpTransport(new Uri($"ws://127.0.0.1:{port}/"));
        await transport.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        await using var messages = transport.ReceiveMessagesAsync(cancellation.Token).GetAsyncEnumerator();

        (await messages.MoveNextAsync()).ShouldBeTrue();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await messages.MoveNextAsync());
    }

    private static async Task ServeTwoFramesAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var webSocket = (await context.AcceptWebSocketAsync(subProtocol: null)).WebSocket;

        await webSocket.SendAsync(Encoding.UTF8.GetBytes("first"), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        await webSocket.SendAsync(Encoding.UTF8.GetBytes("second"), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        var buffer = new byte[256];
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
            }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
