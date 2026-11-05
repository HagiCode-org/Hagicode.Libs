using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Acp;

public sealed class AcpSessionClientImagePromptTests
{
    [Test]
    public async Task SendPromptAsync_text_only_frame_has_one_text_block()
    {
        var transport = new PromptFrameTransport();
        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        await client.SendPromptAsync("session-1", "hi");

        var prompt = transport.PromptFrames.Single()["params"]!["prompt"]!.ToJsonString();
        prompt.ShouldBe("""[{"type":"text","text":"hi"}]""");
    }

    [Test]
    public async Task SendPromptAsync_with_empty_images_sends_the_same_frame_as_text_only()
    {
        var transport = new PromptFrameTransport();
        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        await client.SendPromptAsync("session-1", "hi");
        await client.SendPromptAsync("session-1", "hi", Array.Empty<AcpImageBlock>());

        transport.PromptFrames.Count.ShouldBe(2);
        // Compare params only: the JSON-RPC ids differ between the two requests.
        transport.PromptFrames[1]["params"]!.ToJsonString().ShouldBe(transport.PromptFrames[0]["params"]!.ToJsonString());
    }

    [Test]
    public async Task SendPromptAsync_places_one_png_after_the_text_block()
    {
        var transport = new PromptFrameTransport();
        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        await client.SendPromptAsync("session-1", "describe", [new AcpImageBlock("image/png", "AAEC")]);

        var blocks = transport.PromptFrames.Single()["params"]!["prompt"]!.AsArray();
        blocks.Count.ShouldBe(2);
        blocks[0]!.ToJsonString().ShouldBe("""{"type":"text","text":"describe"}""");
        blocks[1]!.ToJsonString().ShouldBe("""{"type":"image","data":"AAEC","mimeType":"image/png"}""");
    }

    [Test]
    public async Task SendPromptAsync_keeps_three_images_in_the_order_given()
    {
        var transport = new PromptFrameTransport();
        await using var client = new AcpSessionClient(transport);
        await client.ConnectAsync();

        await client.SendPromptAsync(
            "session-1",
            "compare",
            [new AcpImageBlock("image/png", "AAA="), new AcpImageBlock("image/jpeg", "BBB="), new AcpImageBlock("image/gif", "CCC=")]);

        var blocks = transport.PromptFrames.Single()["params"]!["prompt"]!.AsArray();
        blocks.Select(static block => block!["type"]!.GetValue<string>()).ShouldBe(["text", "image", "image", "image"]);
        blocks.Skip(1).Select(static block => block!["mimeType"]!.GetValue<string>()).ShouldBe(["image/png", "image/jpeg", "image/gif"]);
        blocks.Skip(1).Select(static block => block!["data"]!.GetValue<string>()).ShouldBe(["AAA=", "BBB=", "CCC="]);
    }

    [Test]
    public void AcpImageBlock_rejects_blank_fields()
    {
        Should.Throw<ArgumentException>(() => new AcpImageBlock(" ", "AAEC"));
        Should.Throw<ArgumentException>(() => new AcpImageBlock("image/png", ""));
    }

    private sealed class PromptFrameTransport : IAcpTransport
    {
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();

        public List<JsonObject> PromptFrames { get; } = [];

        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            var frame = JsonNode.Parse(message)!.AsObject();
            if (frame["method"]?.GetValue<string>() == "session/prompt")
            {
                PromptFrames.Add(frame);
            }

            if (frame["id"] is { } id)
            {
                var response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["result"] = JsonNode.Parse("""{"stopReason":"end_turn"}""")
                };
                _inbound.Writer.TryWrite(response.ToJsonString());
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_inbound.Reader.TryRead(out var message))
                {
                    yield return message;
                }
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            _inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
