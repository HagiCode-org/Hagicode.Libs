using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Providers.Tests;

// Inline chat images on Pi. The frames come from Fixtures/Pi/rpc-image-*.jsonl, which hold real pi 1.4.0 output recorded on
// 2026-11-04 through omniroute (see the probe notes in repos/hagicode-core/docs/pi-image-attachments.md). None of these tests needs
// pi, credentials or a model: the process is replayed from the recorded lines.
public sealed class PiChatImageRpcTests
{
    private const string TextImageMessage = "Read the text in this image. Reply with the code only.";

    [Test]
    public void Attachment_string_forms_and_json_never_carry_the_bytes()
    {
        var bytes = File.ReadAllBytes(FixturePath("images", "text-code.png"));
        var image = new PiImageAttachment("image/png", bytes);
        var options = new PiOptions { Images = [image] };
        var base64 = Convert.ToBase64String(bytes);

        Assert.That(image.ToString(), Does.Contain("image/png").And.Contain(bytes.Length.ToString()));
        Assert.That(image.ToString(), Does.Not.Contain(base64));
        Assert.That(options.ToString(), Does.Not.Contain(base64));
        Assert.That(JsonSerializer.Serialize(image), Does.Not.Contain(base64));
        Assert.That(JsonSerializer.Serialize(image), Does.Contain("\"MediaType\":\"image/png\"").And.Contain($"\"ByteLength\":{bytes.Length}"));
        Assert.That(new PiOptions().Images, Is.Empty);
    }

    [Test]
    public void Prompt_frame_without_images_is_the_text_only_frame()
    {
        var frame = PiRpcProtocol.BuildPrompt("hi");

        Assert.That(frame, Is.EqualTo("{\"id\":\"hagicode-prompt\",\"type\":\"prompt\",\"message\":\"hi\"}"));
        Assert.That(PiRpcProtocol.BuildPrompt("hi", []), Is.EqualTo(frame));
    }

    [Test]
    public void Prompt_frame_with_an_image_matches_the_recorded_request()
    {
        var bytes = File.ReadAllBytes(FixturePath("images", "text-code.png"));

        var built = JsonDocument.Parse(PiRpcProtocol.BuildPrompt(TextImageMessage, [new PiImageAttachment("image/png", bytes)])).RootElement;
        var recorded = RecordedFrames("rpc-image-text")
            .First(frame => frame.Direction == "request" && frame.Frame.GetProperty("id").GetString() == PiRpcProtocol.PromptRequestId)
            .Frame;

        Assert.That(built.GetProperty("id").GetString(), Is.EqualTo(recorded.GetProperty("id").GetString()));
        Assert.That(built.GetProperty("type").GetString(), Is.EqualTo("prompt"));
        Assert.That(built.GetProperty("message").GetString(), Is.EqualTo(recorded.GetProperty("message").GetString()));

        var builtImage = built.GetProperty("images")[0];
        var recordedImage = recorded.GetProperty("images")[0];
        Assert.That(builtImage.GetProperty("type").GetString(), Is.EqualTo("image"));
        Assert.That(builtImage.GetProperty("mimeType").GetString(), Is.EqualTo(recordedImage.GetProperty("mimeType").GetString()));
        Assert.That(builtImage.GetProperty("data").GetString(), Is.EqualTo(recordedImage.GetProperty("data").GetString()));
        Assert.That(Convert.FromBase64String(builtImage.GetProperty("data").GetString()!), Is.EqualTo(bytes));
    }

    [Test]
    public void Prompt_frame_keeps_image_order()
    {
        var first = new PiImageAttachment("image/png", [1, 2, 3]);
        var second = new PiImageAttachment("image/jpeg", [4, 5]);

        var built = JsonDocument.Parse(PiRpcProtocol.BuildPrompt("two", [first, second])).RootElement;

        Assert.That(built.GetProperty("images")[0].GetProperty("mimeType").GetString(), Is.EqualTo("image/png"));
        Assert.That(built.GetProperty("images")[1].GetProperty("mimeType").GetString(), Is.EqualTo("image/jpeg"));
    }

    [Test]
    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    [TestCase(false, true, true)]
    public void Run_mode_is_rpc_when_images_or_a_usable_handler_are_present(bool withHandler, bool withTools, bool withImages)
    {
        var options = new PiOptions
        {
            UserInputHandler = withHandler ? (_, _) => Task.FromResult<PiUserInputResponse?>(null) : null,
            DisableAllTools = !withTools,
            Images = withImages ? [new PiImageAttachment("image/png", [1])] : []
        };

        var expected = withImages || (withHandler && withTools);
        Assert.That(PiProvider.UsesRpc(options), Is.EqualTo(expected));
    }

    [Test]
    public void Rpc_command_without_a_handler_has_no_extension_and_no_question_tool()
    {
        var provider = NewProvider();
        var options = new PiOptions
        {
            Model = "cc/claude-sonnet-5",
            Provider = "omniroute",
            AllowedTools = ["read"],
            Images = [new PiImageAttachment("image/png", [1, 2, 3])]
        };

        var arguments = provider.BuildRpcCommandArguments(options, questionExtensionPath: null).ToList();

        Assert.That(arguments.Take(2), Is.EqualTo(new[] { "--mode", "rpc" }));
        Assert.That(arguments, Does.Not.Contain("-e"));
        Assert.That(string.Join(' ', arguments), Does.Not.Contain("ask_user_question"));
        Assert.That(string.Join(' ', arguments), Does.Not.Contain("hagicode-question"));
        Assert.That(arguments, Does.Contain("--tools"));
        Assert.That(arguments[arguments.IndexOf("--tools") + 1], Is.EqualTo("read"));
        Assert.That(arguments, Does.Not.Contain("--no-tools"));
    }

    [Test]
    public void Rpc_command_keeps_the_json_options_and_names_no_image_file()
    {
        var provider = NewProvider();
        var options = new PiOptions
        {
            Model = "cc/claude-sonnet-5",
            Provider = "omniroute",
            Thinking = "medium",
            SessionId = "session-42",
            SessionDirectory = "/scratch/sessions",
            SystemPrompt = "Be brief.",
            AllowedTools = ["read", "bash"],
            ExcludedTools = ["write"],
            Images = [new PiImageAttachment("image/png", [1, 2, 3])]
        };

        var json = provider.BuildCommandArguments(options, "prompt text").ToList();
        var rpc = provider.BuildRpcCommandArguments(options, questionExtensionPath: null).ToList();

        // The only differences are the mode flags and the prompt, which the RPC run sends as a command.
        var jsonWithoutMode = json.Skip(3).Take(json.Count - 4).ToArray();
        Assert.That(json.Take(3), Is.EqualTo(new[] { "--mode", "json", "--print" }));
        Assert.That(json[^1], Is.EqualTo("prompt text"));
        Assert.That(rpc.Skip(2), Is.EqualTo(jsonWithoutMode));
        Assert.That(string.Join(' ', rpc), Does.Not.Contain(".png"));
        Assert.That(string.Join(' ', rpc), Does.Not.Contain("prompt text"));
    }

    [Test]
    public void Rpc_command_with_a_handler_still_loads_the_extension_and_adds_the_question_tool()
    {
        var provider = NewProvider();
        var options = new PiOptions { AllowedTools = ["read"] };

        var arguments = provider.BuildRpcCommandArguments(options, questionExtensionPath: "/scratch/question.ts").ToList();

        var extensionIndex = arguments.IndexOf("-e");
        Assert.That(extensionIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(arguments[extensionIndex + 1], Is.EqualTo("/scratch/question.ts"));
        Assert.That(arguments[arguments.IndexOf("--tools") + 1], Does.Contain(PiUserInputRequest.QuestionToolName));
    }

    [Test]
    public async Task Image_run_sends_the_recorded_prompt_and_the_model_reads_the_code()
    {
        var process = new ReplayChatProcess(RecordedStdout("rpc-image-text"));
        var provider = new ReplayProvider(process);
        var bytes = File.ReadAllBytes(FixturePath("images", "text-code.png"));
        var options = new PiOptions
        {
            Model = "cc/claude-sonnet-5",
            Provider = "omniroute",
            NoSession = true,
            Images = [new PiImageAttachment("image/png", bytes)]
        };

        var messages = await CollectAsync(provider, options, TextImageMessage);

        var sent = JsonDocument.Parse(process.Written.Single(line => line.Contains("\"hagicode-prompt\""))).RootElement;
        Assert.That(sent.GetProperty("images")[0].GetProperty("mimeType").GetString(), Is.EqualTo("image/png"));
        Assert.That(provider.RpcContext!.Arguments.Take(2), Is.EqualTo(new[] { "--mode", "rpc" }));
        Assert.That(provider.RpcContext.Arguments, Does.Not.Contain("-e"));
        Assert.That(string.Join('\n', messages.Select(message => message.Content.GetRawText())), Does.Contain("HAGI-7421"));
    }

    [Test]
    public async Task Image_run_never_puts_the_bytes_into_its_messages()
    {
        var process = new ReplayChatProcess(RecordedStdout("rpc-image-quad"));
        var provider = new ReplayProvider(process);
        var bytes = File.ReadAllBytes(FixturePath("images", "quad-colors.png"));
        var options = new PiOptions
        {
            Model = "cc/claude-sonnet-5",
            NoSession = true,
            Images = [new PiImageAttachment("image/png", bytes)]
        };

        var messages = await CollectAsync(provider, options, "Describe the four quadrants.");

        var base64 = Convert.ToBase64String(bytes);
        Assert.That(string.Join('\n', messages.Select(message => message.Content.GetRawText())), Does.Not.Contain(base64));
        // The frame escapes base64 characters such as "+" for JSON; the decoded value is what Pi receives.
        var sentData = process.Written
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(record => record.GetProperty("type").GetString() == "prompt")
            .Select(record => record.GetProperty("images")[0].GetProperty("data").GetString())
            .Single();
        Assert.That(sentData, Is.EqualTo(base64));
    }

    [Test]
    public async Task Image_run_without_a_handler_declines_a_user_extension_dialog()
    {
        var script = RecordedStdout("rpc-image-text").ToList();
        var dialog = "{\"type\":\"extension_ui_request\",\"id\":\"dialog-1\",\"method\":\"select\",\"title\":\"Pick\",\"options\":[\"a\"]}";
        script.Insert(script.FindIndex(line => line.Contains("\"agent_end\"")), dialog);

        var process = new ReplayChatProcess(script);
        var provider = new ReplayProvider(process);
        var options = new PiOptions
        {
            Model = "cc/claude-sonnet-5",
            NoSession = true,
            Images = [new PiImageAttachment("image/png", File.ReadAllBytes(FixturePath("images", "text-code.png")))]
        };

        await CollectAsync(provider, options, TextImageMessage);

        Assert.That(process.Written.Any(line => line.Contains("\"extension_ui_response\"") && line.Contains("\"dialog-1\"") && line.Contains("\"cancelled\":true")), Is.True);
    }

    [Test]
    public async Task Rejected_image_prompt_fails_with_pi_diagnostic_and_no_image_data()
    {
        var script = new List<string>
        {
            "{\"id\":\"hagicode-get-state\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{\"sessionId\":\"s-1\",\"model\":{\"id\":\"auto/best-coding\",\"input\":[\"text\"]}}}",
            "{\"id\":\"hagicode-prompt\",\"type\":\"response\",\"command\":\"prompt\",\"success\":false,\"error\":\"Model does not accept image input\"}"
        };
        var process = new ReplayChatProcess(script);
        var provider = new ReplayProvider(process);
        var bytes = File.ReadAllBytes(FixturePath("images", "text-code.png"));
        var options = new PiOptions
        {
            Model = "auto/best-coding",
            NoSession = true,
            Images = [new PiImageAttachment("image/png", bytes)]
        };

        var messages = await CollectAsync(provider, options, TextImageMessage);

        var text = string.Join('\n', messages.Select(message => message.Content.GetRawText()));
        Assert.That(text, Does.Contain("Model does not accept image input"));
        Assert.That(text, Does.Not.Contain(Convert.ToBase64String(bytes)));
        Assert.That(messages.Any(message => message.Type.Contains("completed", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public void Prompt_echo_with_an_image_block_is_read_as_its_text()
    {
        var echo = JsonDocument.Parse(
            "{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Read it\"},{\"type\":\"image\",\"data\":\"AAAA\",\"mimeType\":\"image/png\"}]}}").RootElement;
        var textOnly = JsonDocument.Parse(
            "{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Read it\"}]}}").RootElement;
        var twoText = JsonDocument.Parse(
            "{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}}").RootElement;

        Assert.That(PiRpcProtocol.ReadUserMessageText(echo), Is.EqualTo("Read it"));
        Assert.That(PiRpcProtocol.ReadUserMessageText(textOnly), Is.EqualTo("Read it"));
        Assert.That(PiRpcProtocol.ReadUserMessageText(twoText), Is.Null);
    }

    private static async Task<List<CliMessage>> CollectAsync(ReplayProvider provider, PiOptions options, string prompt)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static PiProvider NewProvider() => new(new StubExecutableResolver(), new CliProcessManager());

    private static string FixturePath(params string[] parts)
    {
        var segments = new[] { AppContext.BaseDirectory, "Fixtures", "Pi" }.Concat(parts).ToArray();
        return Path.Combine(segments);
    }

    private static IReadOnlyList<RecordedFrame> RecordedFrames(string name)
    {
        return File.ReadLines(FixturePath(name + ".jsonl"))
            .Where(static line => line.Length > 0)
            .Select(static line => JsonDocument.Parse(line).RootElement)
            .Where(static root => root.TryGetProperty("frame", out _))
            .Select(static root => new RecordedFrame(root.GetProperty("dir").GetString()!, root.GetProperty("frame").Clone()))
            .ToList();
    }

    /// <summary>The stdout lines of a recorded run, in order, as Pi wrote them.</summary>
    private static IReadOnlyList<string> RecordedStdout(string name)
    {
        return File.ReadLines(FixturePath(name + ".jsonl"))
            .Where(static line => line.Length > 0)
            .Select(static line => JsonDocument.Parse(line).RootElement)
            .Where(static root => root.TryGetProperty("frame", out _) && root.GetProperty("dir").GetString() == "stdout")
            .Select(static root => root.GetProperty("frame").GetRawText())
            .ToList();
    }

    private readonly record struct RecordedFrame(string Direction, JsonElement Frame);

    /// <summary>
    /// Plays pi from recorded stdout lines. The get_state reply goes out when the handshake is written; every other line goes out
    /// when the prompt is written, so the run goes on the way it does against a live pi. The script's end ends the stream.
    /// </summary>
    private sealed class ReplayChatProcess(IEnumerable<string> script) : IPiRpcProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly List<string> _remaining = [.. script];
        private readonly List<string> _written = [];
        private readonly object _gate = new();

        public IReadOnlyList<string> Written
        {
            get
            {
                lock (_gate)
                {
                    return [.. _written];
                }
            }
        }

        public Task<string> StandardError => Task.FromResult(string.Empty);

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = JsonDocument.Parse(line).RootElement;
            var type = record.GetProperty("type").GetString();
            var id = record.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;

            lock (_gate)
            {
                _written.Add(line);
                if (type == "get_state")
                {
                    EmitUntil(static text => text.Contains("\"hagicode-get-state\""));
                }
                else if (type == "prompt")
                {
                    EmitUntil(static text => false);
                }
                else if (id is not null && type == "extension_ui_response")
                {
                    // Answers to dialogs need no reply in the script.
                }
            }

            return ValueTask.CompletedTask;
        }

        public void CloseInput()
        {
            lock (_gate)
            {
                _output.Writer.TryComplete();
            }
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task StopAsync()
        {
            CloseInput();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            CloseInput();
            return ValueTask.CompletedTask;
        }

        private void EmitUntil(Func<string, bool> stopAfter)
        {
            while (_remaining.Count > 0)
            {
                var next = _remaining[0];
                _remaining.RemoveAt(0);
                _output.Writer.TryWrite(next);
                if (stopAfter(next))
                {
                    return;
                }
            }

            _output.Writer.TryComplete();
        }
    }

    private sealed class ReplayProvider(ReplayChatProcess process) : PiProvider(new StubExecutableResolver(), new CliProcessManager(), null, null)
    {
        public ProcessStartContext? RpcContext { get; private set; }

        internal override ValueTask<IPiRpcProcess> StartRpcProcessAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
        {
            RpcContext = startContext;
            return ValueTask.FromResult<IPiRpcProcess>(process);
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }
}
