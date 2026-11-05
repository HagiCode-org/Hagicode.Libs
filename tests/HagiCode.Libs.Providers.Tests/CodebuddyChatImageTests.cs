using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codebuddy;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Chat image input for the CodeBuddy provider. The frames come from the real codebuddy 2.179.0 captures under
/// Fixtures/Codebuddy. The only synthesized shapes are the image-false and image-missing initialize results, which were not
/// captured live; they are rewritten from the captured image-true result and labelled as such where they are used.
/// </summary>
public sealed class CodebuddyChatImageTests
{
    private const string NoImageDeliveryReason = "connection did not advertise image input";
    private const string PermissionDeniedText = "图片工具权限被拒绝，图片没有被读取。";
    private const string PromptText = "Describe the attached image(s) in detail.";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    [TestCase(1)]
    [TestCase(3)]
    public async Task Capability_true_sends_the_text_block_then_each_image_block_in_attachment_order(int imageCount)
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"));
        var attachments = Enumerable.Range(1, imageCount).Select(index => PngAttachment($"img-{index}")).ToArray();

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), attachments, PromptText);

        var blocks = PromptBlocks(transport);
        blocks.Count.ShouldBe(imageCount + 1);
        blocks[0]!["type"]!.GetValue<string>().ShouldBe("text");
        blocks[0]!["text"]!.GetValue<string>().ShouldBe(PromptText);
        for (var index = 1; index <= imageCount; index++)
        {
            blocks[index]!["type"]!.GetValue<string>().ShouldBe("image");
            blocks[index]!["mimeType"]!.GetValue<string>().ShouldBe("image/png");
            blocks[index]!["data"]!.GetValue<string>().ShouldBe(Convert.ToBase64String(PngBytes()));
        }

        var delivery = DeliveryMessage(messages);
        delivery.GetProperty("images").GetArrayLength().ShouldBe(imageCount);
        foreach (var image in delivery.GetProperty("images").EnumerateArray())
        {
            image.GetProperty("delivery").GetString().ShouldBe("inline");
            image.TryGetProperty("reason", out var reason).ShouldBeTrue();
            reason.ValueKind.ShouldBe(JsonValueKind.Null);
        }
        messages.Select(static message => message.Type).ShouldContain("terminal.completed");
    }

    [TestCase("false")]
    [TestCase("missing-image")]
    [TestCase("non-boolean")]
    [TestCase("missing-prompt-capabilities")]
    public async Task Capability_false_or_absent_sends_text_only_with_manifest_lines_and_reports_the_reason(string variant)
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor(variant), PromptResultFor("completed"));
        var attachments = new[] { PngAttachment("img-1"), PngAttachment("img-2") };

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), attachments, PromptText);

        var blocks = PromptBlocks(transport);
        blocks.Count.ShouldBe(1);
        var text = blocks[0]!["text"]!.GetValue<string>();
        text.ShouldStartWith(PromptText);
        text.ShouldContain(attachments[0].ManifestLine);
        text.ShouldContain(attachments[1].ManifestLine);
        text.ShouldEndWith(attachments[1].ManifestLine);

        var images = DeliveryMessage(messages).GetProperty("images").EnumerateArray().ToArray();
        images.Length.ShouldBe(2);
        foreach (var image in images)
        {
            image.GetProperty("delivery").GetString().ShouldBe("manifest");
            image.GetProperty("reason").GetString().ShouldBe(NoImageDeliveryReason);
        }
    }

    [Test]
    public async Task A_top_level_prompt_capabilities_image_true_is_accepted_as_well()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("top-level-true"), PromptResultFor("completed"));

        await RunAsync(new CodebuddyImageTestProvider(transport), [PngAttachment("img-1")], PromptText);

        PromptBlocks(transport).Count.ShouldBe(2);
        PromptBlocks(transport)[1]!["type"]!.GetValue<string>().ShouldBe("image");
    }

    [Test]
    public async Task Attachments_failing_validation_are_listed_and_the_valid_ones_are_still_inlined_in_order()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"));
        var unsupported = new CodebuddyImageAttachment("img-b", "image/bmp", PngBytes(), "- img-b: image/bmp (listed)");
        var oversized = new CodebuddyImageAttachment("img-c", "image/png", new byte[AcpImageInputLimits.MaxImageBytes + 1], "- img-c: too large (listed)");
        var attachments = new[]
        {
            PngAttachment("img-a"),
            unsupported,
            oversized,
            new CodebuddyImageAttachment("img-d", "image/jpeg", PngBytes(), "- img-d: jpeg (unused)")
        };

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), attachments, PromptText);

        var blocks = PromptBlocks(transport);
        blocks.Count.ShouldBe(3);
        blocks[1]!["mimeType"]!.GetValue<string>().ShouldBe("image/png");
        blocks[2]!["mimeType"]!.GetValue<string>().ShouldBe("image/jpeg");

        var text = blocks[0]!["text"]!.GetValue<string>();
        text.ShouldContain(unsupported.ManifestLine);
        text.ShouldContain(oversized.ManifestLine);
        text.IndexOf(unsupported.ManifestLine, StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf(oversized.ManifestLine, StringComparison.Ordinal));

        var images = DeliveryMessage(messages).GetProperty("images").EnumerateArray().ToArray();
        images.Select(static image => image.GetProperty("delivery").GetString()).ShouldBe(["inline", "manifest", "manifest", "inline"]);
        images[1].GetProperty("reason").GetString().ShouldBe("unsupported media type");
        images[2].GetProperty("reason").GetString().ShouldBe("image exceeds the 5 MiB limit");
    }

    [Test]
    public async Task The_count_cap_of_ten_images_lists_the_eleventh()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"));
        var attachments = Enumerable.Range(1, 11).Select(index => PngAttachment($"img-{index}")).ToArray();

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), attachments, PromptText);

        PromptBlocks(transport).Count.ShouldBe(11);
        var images = DeliveryMessage(messages).GetProperty("images").EnumerateArray().ToArray();
        images.Count(static image => image.GetProperty("delivery").GetString() == "inline").ShouldBe(10);
        images[10].GetProperty("delivery").GetString().ShouldBe("manifest");
        images[10].GetProperty("reason").GetString().ShouldBe("image count limit of 10 reached");
    }

    [Test]
    public async Task The_total_cap_of_twenty_mebibytes_lists_the_image_that_would_exceed_it()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"));
        var largeBytes = new byte[9 * 512 * 1024]; // 4.5 MiB: under the per-image cap, five of them exceed the 20 MiB total
        var attachments = Enumerable.Range(1, 5)
            .Select(index => new CodebuddyImageAttachment($"img-{index}", "image/png", largeBytes, $"- img-{index}: large (listed)"))
            .ToArray();

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), attachments, PromptText);

        PromptBlocks(transport).Count.ShouldBe(5);
        var images = DeliveryMessage(messages).GetProperty("images").EnumerateArray().ToArray();
        images.Select(static image => image.GetProperty("delivery").GetString()).ShouldBe(["inline", "inline", "inline", "inline", "manifest"]);
        images[4].GetProperty("reason").GetString().ShouldBe("total image size exceeds the 20 MiB limit");
    }

    [Test]
    public async Task Without_attachments_the_session_prompt_frame_is_the_text_only_frame()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"));

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), [], "hello");

        var promptFrame = transport.SentLines.Single(static line => line.Contains("\"method\":\"session/prompt\"", StringComparison.Ordinal));
        // The text-only overload writes the same frame it wrote before image support existed: one text block, nothing else.
        Regex.IsMatch(
                promptFrame,
                """^\{"jsonrpc":"2\.0","id":"\d+","method":"session/prompt","params":\{"sessionId":"session-1","prompt":\[\{"type":"text","text":"hello"\}\]\}\}$""")
            .ShouldBeTrue(promptFrame);
        messages.Select(static message => message.Type).ShouldNotContain("image.delivery");
    }

    [Test]
    public async Task With_the_preference_the_view_image_permission_is_answered_with_the_one_time_allowance()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"), requestViewImagePermission: true);

        var messages = await RunAsync(
            new CodebuddyImageTestProvider(transport),
            [PngAttachment("img-1")],
            PromptText,
            preferOneTimePermission: true);

        PermissionOptionIdAnswer(transport).ShouldBe("allow");
        messages.Select(static message => message.Type).ShouldContain("terminal.completed");
    }

    [Test]
    public async Task Without_the_preference_the_view_image_permission_is_answered_with_the_permanent_allowance()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"), requestViewImagePermission: true);

        await RunAsync(new CodebuddyImageTestProvider(transport), [PngAttachment("img-1")], PromptText);

        PermissionOptionIdAnswer(transport).ShouldBe("allow_always");
    }

    [Test]
    public async Task A_permission_denied_cancellation_is_a_failure_that_names_the_denial_and_completes_nothing()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("permission-denied"));

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), [PngAttachment("img-1")], PromptText);

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("text").GetString().ShouldBe(PermissionDeniedText + " (Permission for the image tool was denied, so the image was not read.)");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("cancelled");
        messages.Select(static message => message.Type).ShouldNotContain("terminal.completed");
    }

    [Test]
    public async Task A_plain_cancellation_keeps_its_previous_mapping()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), JsonNode.Parse("""{"stopReason":"cancelled"}""")!);

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), [], "hello");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("cancelled");
        terminal.Content.GetProperty("text").ValueKind.ShouldBe(JsonValueKind.Null);
        terminal.Content.TryGetProperty("message", out _).ShouldBeFalse();
    }

    [Test]
    public async Task The_delivery_result_and_the_attachment_text_carry_no_bytes_or_base64()
    {
        var transport = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"));
        var attachment = PngAttachment("img-1");
        var base64Prefix = Convert.ToBase64String(PngBytes())[..40];

        var messages = await RunAsync(new CodebuddyImageTestProvider(transport), [attachment], PromptText);

        DeliveryMessage(messages).GetRawText().ShouldNotContain(base64Prefix);
        messages.Single(static message => message.Type == "image.delivery").Content.GetRawText().ShouldNotContain(base64Prefix);
        var summary = attachment.ToString();
        summary.ShouldContain("img-1");
        summary.ShouldContain("image/png");
        summary.ShouldNotContain(base64Prefix);
    }

    [Test]
    public async Task Each_execution_uses_the_capability_of_its_own_connection()
    {
        var accepting = new CodebuddyImageScriptedTransport(InitializeResultFor("true"), PromptResultFor("completed"));
        var refusing = new CodebuddyImageScriptedTransport(InitializeResultFor("false"), PromptResultFor("completed"));
        var provider = new CodebuddyImageTestProvider(accepting, refusing);

        await RunAsync(provider, [PngAttachment("img-1")], PromptText);
        await RunAsync(provider, [PngAttachment("img-1")], PromptText);

        PromptBlocks(accepting).Count.ShouldBe(2);
        PromptBlocks(refusing).Count.ShouldBe(1);
    }

    private static async Task<List<CliMessage>> RunAsync(
        CodebuddyImageTestProvider provider,
        IReadOnlyList<CodebuddyImageAttachment> attachments,
        string prompt,
        bool preferOneTimePermission = false)
    {
        using var timeout = new CancellationTokenSource(RunTimeout);
        var options = new CodebuddyOptions
        {
            ExecutablePath = "/custom/codebuddy",
            ImageAttachments = attachments,
            PreferOneTimePermission = preferOneTimePermission
        };
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, timeout.Token))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static JsonArray PromptBlocks(CodebuddyImageScriptedTransport transport)
    {
        return transport.SentFrames
            .Single(static frame => frame["method"]?.GetValue<string>() == "session/prompt")!["params"]!["prompt"]!
            .AsArray();
    }

    private static JsonElement DeliveryMessage(IEnumerable<CliMessage> messages)
    {
        return messages.Single(static message => message.Type == "image.delivery").Content;
    }

    private static string PermissionOptionIdAnswer(CodebuddyImageScriptedTransport transport)
    {
        var answer = transport.SentFrames.Single(static frame => frame["method"] is null && frame["result"] is not null);
        return answer["result"]!["outcome"]!["optionId"]!.GetValue<string>();
    }

    private static CodebuddyImageAttachment PngAttachment(string imageId)
    {
        return new CodebuddyImageAttachment(imageId, "image/png", PngBytes(), $"- {imageId}: image/png at /tmp/{imageId}.png (listed)");
    }

    /// <summary>The 1.5 test image from the captured session/prompt request, decoded back to bytes.</summary>
    private static byte[] PngBytes()
    {
        var data = PromptRequestFixture()["params"]!["prompt"]![1]!["data"]!.GetValue<string>();
        return Convert.FromBase64String(data);
    }

    private static JsonNode PromptRequestFixture() => LoadFixture("session-prompt-image-request");

    /// <summary>
    /// The initialize result for one capability variant. <c>true</c> is the captured frame; the others are rewrites of it, and
    /// the comments say which shape was not captured live.
    /// </summary>
    private static JsonNode InitializeResultFor(string variant)
    {
        var root = LoadFixture("initialize-image-true")["result"]!.DeepClone()!.AsObject();
        var promptCapabilities = root["agentCapabilities"]!["promptCapabilities"]!.AsObject();
        switch (variant)
        {
            case "true":
                break;
            case "false":
                // Synthesized: codebuddy 2.179.0 advertises image true, so no captured frame shows false.
                promptCapabilities["image"] = JsonValue.Create(false);
                break;
            case "missing-image":
                // Synthesized: the image key removed from the captured frame.
                promptCapabilities.Remove("image");
                break;
            case "non-boolean":
                // Synthesized: a non-boolean value must count as false.
                promptCapabilities["image"] = JsonValue.Create("yes");
                break;
            case "missing-prompt-capabilities":
                // Synthesized: no prompt capabilities at all.
                root.Remove("agentCapabilities");
                break;
            case "top-level-true":
                // Synthesized: the captured capability moved to the top level, which the parser also accepts.
                root.Remove("agentCapabilities");
                root["promptCapabilities"] = new JsonObject { ["image"] = JsonValue.Create(true) };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }

        return root;
    }

    private static JsonNode PromptResultFor(string variant)
    {
        return variant switch
        {
            "completed" => LoadFixture("session-prompt-image-completed")["result"]!.DeepClone(),
            "permission-denied" => LoadFixture("session-prompt-permission-denied")["result"]!.DeepClone(),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
        };
    }

    private static JsonNode LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codebuddy", name + ".json");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    private sealed class PassThroughExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    /// <summary>Hands each execution the next scripted transport, so a test can run several connections in order.</summary>
    private sealed class CodebuddyImageTestProvider : CodebuddyProvider
    {
        private readonly Queue<IAcpTransport> _transports;

        public CodebuddyImageTestProvider(params CodebuddyImageScriptedTransport[] transports)
            : base(new PassThroughExecutableResolver(), new CliProcessManager())
        {
            _transports = new Queue<IAcpTransport>(transports);
        }

        protected override IAcpTransport CreateAcpTransport(ProcessStartContext startContext) => _transports.Dequeue();
    }
}

/// <summary>
/// Replays the CodeBuddy ACP replies the image tests need. Each reply is the captured result with its request id rewritten.
/// When <see cref="requestViewImagePermission" /> is set, the <c>session/prompt</c> reply waits until the client has answered
/// the captured <c>ViewImage</c> permission request, so the answer is observed before the run completes.
/// </summary>
internal sealed class CodebuddyImageScriptedTransport(
    JsonNode initializeResult,
    JsonNode promptResult,
    bool requestViewImagePermission = false) : IAcpTransport
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly object _sync = new();
    private readonly List<string> _sentLines = [];
    private JsonNode? _pendingPromptId;

    /// <summary>Every frame the client sent as raw text, in order.</summary>
    public IReadOnlyList<string> SentLines
    {
        get
        {
            lock (_sync)
            {
                return _sentLines.ToArray();
            }
        }
    }

    /// <summary>Every frame the client sent, parsed.</summary>
    public IReadOnlyList<JsonObject> SentFrames => SentLines.Select(static line => JsonNode.Parse(line)!.AsObject()).ToArray();

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _sentLines.Add(message);
        }

        var frame = JsonNode.Parse(message)!.AsObject();
        var method = frame["method"]?.GetValue<string>();
        if (method is null)
        {
            // The client's answer to the captured permission request: now the held prompt reply can go out.
            if (_pendingPromptId is { } pendingId)
            {
                _pendingPromptId = null;
                ReplyPrompt(pendingId);
            }

            return Task.CompletedTask;
        }

        var id = frame["id"]!.DeepClone();
        switch (method)
        {
            case "initialize":
                Reply(initializeResult, id);
                break;
            case "session/new":
                Reply(JsonNode.Parse("""{"sessionId":"session-1"}"""), id);
                break;
            case "session/prompt":
                if (requestViewImagePermission)
                {
                    _pendingPromptId = id;
                    Enqueue(LoadPermissionRequest());
                }
                else
                {
                    ReplyPrompt(id);
                }

                break;
            default:
                Reply(JsonNode.Parse("{}"), id);
                break;
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
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
        IsConnected = false;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void ReplyPrompt(JsonNode id) => Reply(promptResult, id);

    private void Reply(JsonNode? result, JsonNode id)
    {
        Enqueue(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result?.DeepClone()
        });
    }

    private void Enqueue(JsonObject frame)
    {
        _inbound.Writer.TryWrite(frame.ToJsonString());
    }

    private static JsonObject LoadPermissionRequest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codebuddy", "session-request-permission-view-image.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
