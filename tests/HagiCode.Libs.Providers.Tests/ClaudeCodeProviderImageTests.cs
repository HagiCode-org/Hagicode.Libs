using System.Text.Json;
using HagiCode.Libs.Providers.ClaudeCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>Image attachments on the Claude prompt turn: wire shape, validation before launch, resend, replay and diagnostics.</summary>
public sealed partial class ClaudeCodeProviderTests
{
    private static readonly byte[] FakePngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] FakeJpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6];
    private static readonly string FakePngBase64 = Convert.ToBase64String(FakePngBytes);
    private static readonly string FakeJpegBase64 = Convert.ToBase64String(FakeJpegBytes);

    [Test]
    public async Task ExecuteAsync_keeps_the_prompt_as_a_plain_string_when_no_images_are_attached()
    {
        var provider = CreateProvider();

        await CollectAsync(provider, new ClaudeCodeOptions { SessionId = "session-1" });

        var user = provider.SentMessages.Single(static message => message.Type == "user");
        user.Content.GetRawText().ShouldBe("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello\"},\"session_id\":\"session-1\"}");
    }

    [Test]
    public async Task ExecuteAsync_writes_a_text_block_then_base64_image_blocks_in_order()
    {
        var provider = CreateProvider();
        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            Images = [new ClaudeImageAttachment("image/png", FakePngBytes), new ClaudeImageAttachment("IMAGE/JPEG", FakeJpegBytes)],
        };

        await CollectAsync(provider, options);

        var user = provider.SentMessages.Single(static message => message.Type == "user");
        user.Content.GetRawText().ShouldBe(
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"hello\"},"
            + $"{{\"type\":\"image\",\"source\":{{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"{FakePngBase64}\"}}}},"
            + $"{{\"type\":\"image\",\"source\":{{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":\"{FakeJpegBase64}\"}}}}"
            + "]},\"session_id\":\"session-1\"}");
    }

    [TestCase("image/svg+xml")]
    [TestCase("image/bmp")]
    [TestCase("")]
    public async Task ExecuteAsync_rejects_an_unsupported_media_type_before_any_process_starts(string mediaType)
    {
        var provider = CreateProvider();
        var options = new ClaudeCodeOptions { Images = [new ClaudeImageAttachment(mediaType, FakePngBytes)] };

        var exception = await Should.ThrowAsync<ArgumentException>(() => CollectAsync(provider, options));

        exception.ParamName.ShouldBe("options");
        provider.CreatedTransportCount.ShouldBe(0);
        provider.StartContexts.ShouldBeEmpty();
        exception.Message.ShouldNotContain(FakePngBase64);
    }

    [Test]
    public async Task ExecuteAsync_resend_after_a_missed_start_writes_the_same_image_blocks()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    StreamMessage("result", new { is_error = false, result = "" })
                ]
            ],
            transportFailures:
            [
                new TransportFailure(
                    EchoPrompt: false,
                    ReplyAfterResend:
                    [
                        StreamMessage("system", new { subtype = "init" }),
                        StreamMessage("result", new { is_error = false, result = "resent" })
                    ])
            ]);
        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            PromptStartTimeout = TimeSpan.FromMilliseconds(200),
            Images = [new ClaudeImageAttachment("image/png", FakePngBytes)],
        };

        await CollectAsync(provider, options);

        var users = provider.SentMessages.Where(static message => message.Type == "user").ToList();
        users.Count.ShouldBe(2);
        users[0].Content.GetRawText().ShouldContain("\"type\":\"image\"");
        users[1].Content.GetRawText().ShouldBe(users[0].Content.GetRawText());
    }

    [Test]
    public async Task Replayed_prompt_with_image_blocks_is_not_yielded_and_completion_is_unchanged()
    {
        var replay = StreamMessage("user", new
        {
            isReplay = true,
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "hello" },
                    new { type = "image", source = new { type = "base64", media_type = "image/png", data = FakePngBase64 } },
                },
            },
        });
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    replay,
                    StreamMessage("assistant", new { content = "hi" }),
                    StreamMessage("result", new { is_error = false, result = "hi" })
                ]
            ]);
        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            Images = [new ClaudeImageAttachment("image/png", FakePngBytes)],
        };

        var messages = await CollectAsync(provider, options);

        DescribeTypes(messages).ShouldBe(["system:init", "assistant", "result"]);
        messages.ShouldAllBe(message => !message.Content.GetRawText().Contains(FakePngBase64, StringComparison.Ordinal));
    }

    [Test]
    public async Task A_turn_with_images_puts_no_base64_into_yielded_messages_or_option_diagnostics()
    {
        var provider = CreateProvider(
            messageBatches:
            [
                [
                    StreamMessage("system", new { subtype = "init" }),
                    StreamMessage("assistant", new { content = "seen" }),
                    StreamMessage("result", new { is_error = false, result = "seen" })
                ]
            ]);
        var options = new ClaudeCodeOptions
        {
            SessionId = "session-1",
            Images = [new ClaudeImageAttachment("image/png", FakePngBytes)],
        };

        var messages = await CollectAsync(provider, options);

        messages.ShouldAllBe(message => !message.Content.GetRawText().Contains(FakePngBase64, StringComparison.Ordinal));
        options.ToString().ShouldNotContain(FakePngBase64);
        options.Images[0].ToString().ShouldNotContain(FakePngBase64);
        options.Images[0].ToString().ShouldContain("ByteLength = 11");
        JsonSerializer.Serialize(options).ShouldNotContain(FakePngBase64);
        JsonSerializer.Serialize(options.Images[0]).ShouldBe("{\"MediaType\":\"image/png\"}");
    }
}
