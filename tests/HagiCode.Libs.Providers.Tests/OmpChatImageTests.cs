using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Omp;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Chat images on OMP (openspec omp-chat-image-support, tasks 2.1 to 2.5). The frames come from the real omp captures of
// 2026-11-04 (Fixtures/Omp/rpc-ui-*.jsonl, omp 18.14.1): the request frame shape, the success and rejection replies, the
// cancelled select with no handler. Replayed through a scripted process, so none of these tests needs omp or a model.
public sealed class OmpChatImageTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);
    private static readonly byte[] ImageBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04];
    private static readonly string ImageBase64 = Convert.ToBase64String(ImageBytes);

    // ------------------------------------------------------------------------------------------------ 2.1 attachment

    [Test]
    public void Attachment_prints_and_serializes_without_its_bytes()
    {
        var image = new OmpImageAttachment("image/png", ImageBytes);

        image.ToString().ShouldContain("MediaType = image/png");
        image.ToString().ShouldContain("ByteLength = 12");
        image.ToString().ShouldNotContain(ImageBase64);

        var json = JsonSerializer.Serialize(image);
        json.ShouldContain("image/png");
        json.ShouldNotContain(ImageBase64);
        json.ShouldNotContain("Bytes");
    }

    [Test]
    public void Options_print_without_the_image_bytes()
    {
        var options = new OmpOptions { Images = [new OmpImageAttachment("image/png", ImageBytes)] };

        options.ToString().ShouldNotContain(ImageBase64);
        options.Images.ShouldNotBeEmpty();
        new OmpOptions().Images.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------------------------------------ 2.2 prompt frame

    [Test]
    public void Prompt_without_images_is_the_text_only_frame_byte_for_byte()
    {
        const string expected = "{\"id\":\"hagicode-prompt\",\"type\":\"prompt\",\"message\":\"hello\"}";

        OmpRpcProtocol.BuildPrompt("hello").ShouldBe(expected);
        OmpRpcProtocol.BuildPrompt("hello", []).ShouldBe(expected);
    }

    [Test]
    public void Prompt_with_images_carries_type_data_and_media_type_in_order()
    {
        var second = new OmpImageAttachment("image/jpeg", [9, 8, 7]);
        var frame = JsonDocument.Parse(OmpRpcProtocol.BuildPrompt(
            "look",
            [new OmpImageAttachment("image/png", ImageBytes), second])).RootElement;

        frame.GetProperty("message").GetString().ShouldBe("look");
        var images = frame.GetProperty("images").EnumerateArray().ToArray();
        images.Length.ShouldBe(2);
        images[0].GetProperty("type").GetString().ShouldBe("image");
        images[0].GetProperty("data").GetString().ShouldBe(ImageBase64);
        images[0].GetProperty("mimeType").GetString().ShouldBe("image/png");
        images[1].GetProperty("data").GetString().ShouldBe(Convert.ToBase64String(second.Bytes));
        images[1].GetProperty("mimeType").GetString().ShouldBe("image/jpeg");
    }

    [Test]
    public void Prompt_with_images_has_the_same_shape_as_the_captured_omp_request()
    {
        // The capture sent the prompt with one PNG; its request frame is the reference for the keys and their value types.
        var captured = ReadCapturedRequests("rpc-ui-ask-text-success")
            .Single(static frame => frame.GetProperty("type").GetString() == "prompt");
        var ours = JsonDocument.Parse(OmpRpcProtocol.BuildPrompt(
            captured.GetProperty("message").GetString()!,
            [new OmpImageAttachment("image/png", ImageBytes)])).RootElement;

        ours.EnumerateObject().Select(static property => property.Name).ShouldBe(
            captured.EnumerateObject().Select(static property => property.Name), ignoreOrder: true);

        var capturedImage = captured.GetProperty("images")[0];
        var ourImage = ours.GetProperty("images")[0];
        ourImage.EnumerateObject().Select(static property => property.Name).ShouldBe(
            capturedImage.EnumerateObject().Select(static property => property.Name), ignoreOrder: true);
        ourImage.GetProperty("mimeType").GetString().ShouldBe(capturedImage.GetProperty("mimeType").GetString());
    }

    // ------------------------------------------------------------------------------------------------ 2.3 command line

    [TestCase(false, false, false, false)]
    [TestCase(false, true, false, true)]
    [TestCase(true, false, false, true)]
    [TestCase(true, true, false, true)]
    [TestCase(true, false, true, true)]
    [TestCase(false, false, true, false)]
    public void UsesRpc_is_true_for_images_or_for_a_handler_with_tools(bool withImages, bool withHandler, bool disableAllTools, bool expected)
    {
        var options = Options(withImages, withHandler, disableAllTools);

        OmpProvider.UsesRpc(options).ShouldBe(expected);
    }

    [Test]
    public void Images_without_a_handler_run_rpc_ui_without_the_ask_tool_and_never_name_an_image_file()
    {
        var provider = new ScriptedProvider(new ReplayRpcProcess([]));
        var options = new OmpOptions
        {
            Model = "omniroute/claude/claude-haiku-5-5",
            Images = [new OmpImageAttachment("image/png", ImageBytes)]
        };

        var arguments = provider.BuildRpcCommandArguments(options).ToList();

        arguments.Take(2).ShouldBe(new[] { "--mode", "rpc-ui" });
        arguments.ShouldNotContain(OmpUserInputRequest.QuestionToolName);
        arguments.ShouldNotContain("--tools");
        arguments.ShouldNotContain("--print");
        arguments.Any(static argument => argument.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Test]
    public void Images_with_a_handler_keep_the_question_tool_on_a_non_empty_allowlist()
    {
        var provider = new ScriptedProvider(new ReplayRpcProcess([]));
        var options = new OmpOptions
        {
            AllowedTools = ["read"],
            UserInputHandler = NoAnswer,
            Images = [new OmpImageAttachment("image/png", ImageBytes)]
        };

        var arguments = provider.BuildRpcCommandArguments(options).ToList();

        arguments.ShouldContain("--tools");
        arguments[arguments.IndexOf("--tools") + 1].ShouldBe("read,ask");
    }

    [Test]
    public void Images_with_every_tool_disabled_stay_rpc_ui_with_no_tools()
    {
        var provider = new ScriptedProvider(new ReplayRpcProcess([]));
        var options = new OmpOptions
        {
            DisableAllTools = true,
            UserInputHandler = NoAnswer,
            Images = [new OmpImageAttachment("image/png", ImageBytes)]
        };

        var arguments = provider.BuildRpcCommandArguments(options);

        arguments.Take(2).ShouldBe(new[] { "--mode", "rpc-ui" });
        arguments.ShouldContain("--no-tools");
        arguments.ShouldNotContain(OmpUserInputRequest.QuestionToolName);
    }

    [Test]
    public void Rpc_command_differs_from_the_print_command_only_in_mode_and_prompt()
    {
        var provider = new ScriptedProvider(new ReplayRpcProcess([]));
        var options = new OmpOptions
        {
            Provider = "omniroute",
            Model = "claude/claude-haiku-5-5",
            Thinking = "medium",
            SessionId = "resume-me",
            SystemPrompt = "be brief",
            AppendSystemPrompts = ["one"],
            PluginDirectories = ["/plugins/a"],
            Images = [new OmpImageAttachment("image/png", ImageBytes)]
        };

        var print = provider.BuildCommandArguments(options, "PROMPT").ToList();
        var rpc = provider.BuildRpcCommandArguments(options).ToList();

        // Print: --mode json --print <options> PROMPT. RPC: --mode rpc-ui <options>. Everything in between is the same.
        rpc.Skip(2).ShouldBe(print.Skip(3).Take(print.Count - 4));
    }

    // ------------------------------------------------------------------------------------------------ 2.4 session

    [Test]
    public async Task Image_run_without_a_handler_starts_at_get_state_and_sends_the_images_on_prompt()
    {
        var process = new ReplayRpcProcess(ReadCapturedFrames("rpc-ui-noask-text-success"));
        var provider = new ScriptedProvider(process);

        var messages = await RunAsync(provider, Options(withImages: true, withHandler: false), "read it");

        var written = process.Written.Select(ParseFrame).ToList();
        written.Select(static frame => frame.GetProperty("type").GetString()).ShouldBe(new[] { "get_state", "prompt" });
        written[1].GetProperty("images")[0].GetProperty("data").GetString().ShouldBe(ImageBase64);
        messages.Last().Type.ShouldBe("terminal.completed");
        messages.Select(static message => message.Content.ToString()).ShouldNotContain(
            static text => text.Contains(ImageBase64, StringComparison.Ordinal));
    }

    [Test]
    public async Task Image_run_with_a_handler_enables_the_ask_dialog_before_get_state()
    {
        var process = new ReplayRpcProcess(ReadCapturedFrames("rpc-ui-ask-text-success"));
        var provider = new ScriptedProvider(process);

        await RunAsync(provider, Options(withImages: true, withHandler: true), "read it");

        process.Written.Select(ParseFrame).Select(static frame => frame.GetProperty("type").GetString())
            .ShouldBe(new[] { "set_ask_dialog", "get_state", "prompt" });
    }

    [Test]
    public async Task Image_run_with_every_tool_disabled_sends_no_ask_dialog()
    {
        var process = new ReplayRpcProcess(ReadCapturedFrames("rpc-ui-noask-text-success"));
        var provider = new ScriptedProvider(process);

        await RunAsync(provider, Options(withImages: true, withHandler: true, disableAllTools: true), "read it");

        process.Written.Select(ParseFrame).Select(static frame => frame.GetProperty("type").GetString())
            .ShouldBe(new[] { "get_state", "prompt" });
    }

    [Test]
    public async Task Rejected_prompt_fails_the_run_and_keeps_omp_diagnostic_without_image_bytes()
    {
        var process = new ReplayRpcProcess(
        [
            StateReply(),
            "{\"id\":\"hagicode-prompt\",\"type\":\"response\",\"command\":\"prompt\",\"success\":false,\"error\":\"model cannot read images\"}"
        ]);
        var provider = new ScriptedProvider(process);

        var messages = await RunAsync(provider, Options(withImages: true, withHandler: false), "read it");

        messages.Last().Type.ShouldBe("terminal.failed");
        messages.Last().Content.ToString().ShouldContain("model cannot read images");
        messages.Select(static message => message.Content.ToString()).ShouldNotContain(
            static text => text.Contains(ImageBase64, StringComparison.Ordinal));
    }

    [Test]
    public async Task Question_cancelled_without_a_handler_ends_as_a_normal_completion()
    {
        var process = new ReplayRpcProcess(ReadCapturedFrames("rpc-ui-noask-ask-cancelled"));
        var provider = new ScriptedProvider(process);

        var messages = await RunAsync(provider, Options(withImages: true, withHandler: false), "ask me");

        // The select dialog of the fallback is answered as cancelled, and OMP ends the run as aborted: a normal end, not a failure.
        var answers = process.Written.Select(ParseFrame)
            .Where(static frame => frame.GetProperty("type").GetString() == "extension_ui_response")
            .ToList();
        answers.ShouldNotBeEmpty();
        answers.All(static frame => frame.TryGetProperty("cancelled", out var cancelled) && cancelled.GetBoolean()).ShouldBeTrue();
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [TestCase("rpc-ui-ask-text-success")]
    [TestCase("rpc-ui-ask-quad-success")]
    public void The_echo_of_a_prompt_with_images_is_never_read_as_a_steer(string name)
    {
        // The echo carries a text block and an image block, and no steering flag: it must not count as a steer of the run.
        var echoes = ReadCapturedFrames(name)
            .Select(ParseFrame)
            .Where(static frame => frame.GetProperty("type").GetString() == "message_start"
                                   && frame.TryGetProperty("message", out var message)
                                   && message.GetProperty("role").GetString() == "user")
            .ToList();

        echoes.ShouldNotBeEmpty();
        foreach (var echo in echoes)
        {
            OmpRpcProtocol.ReadSteeringUserMessageText(echo).ShouldBeNull();
        }
    }

    // ------------------------------------------------------------------------------------------------ 2.5 fallback

    [Test]
    public async Task Rejected_ask_dialog_with_images_retries_in_rpc_ui_without_json()
    {
        var rejected = new ReplayRpcProcess(
        [
            "{\"id\":\"hagicode-set-ask-dialog\",\"type\":\"response\",\"command\":\"set_ask_dialog\",\"success\":false,\"error\":\"unknown command\"}"
        ]);
        var retried = new ReplayRpcProcess(ReadCapturedFrames("rpc-ui-noask-text-success"));
        var provider = new ScriptedProvider(rejected, retried);

        var messages = await RunAsync(provider, Options(withImages: true, withHandler: true), "read it");

        provider.RpcStarts.ShouldBe(2);
        provider.PrintStarts.ShouldBe(0);
        rejected.Written.Select(ParseFrame).Select(static frame => frame.GetProperty("type").GetString()).ShouldBe(new[] { "set_ask_dialog" });
        retried.Written.Select(ParseFrame).Select(static frame => frame.GetProperty("type").GetString()).ShouldBe(new[] { "get_state", "prompt" });
        provider.RetryArguments!.ShouldContain("rpc-ui");
        provider.RetryArguments!.ShouldNotContain("--print");
        provider.RetryArguments!.ShouldNotContain(OmpUserInputRequest.QuestionToolName);
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task Rejected_ask_dialog_without_images_still_falls_back_to_json()
    {
        var rejected = new ReplayRpcProcess(
        [
            "{\"id\":\"hagicode-set-ask-dialog\",\"type\":\"response\",\"command\":\"set_ask_dialog\",\"success\":false,\"error\":\"unknown command\"}"
        ]);
        var provider = new ScriptedProvider(rejected);

        await RunAsync(provider, Options(withImages: false, withHandler: true), "read it");

        provider.RpcStarts.ShouldBe(1);
        provider.PrintStarts.ShouldBe(1);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static OmpOptions Options(bool withImages, bool withHandler, bool disableAllTools = false) => new()
    {
        Model = "omniroute/cc/claude-sonnet-5",
        DisableAllTools = disableAllTools,
        UserInputHandler = withHandler ? NoAnswer : null,
        QuestionTimeout = TestTimeout,
        Images = withImages ? [new OmpImageAttachment("image/png", ImageBytes)] : []
    };

    private static Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> NoAnswer =>
        static (_, _) => Task.FromResult<OmpUserInputResponse?>(null);

    private static async Task<List<CliMessage>> RunAsync(OmpProvider provider, OmpOptions options, string prompt)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, timeout.Token))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static string StateReply() =>
        "{\"id\":\"hagicode-get-state\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{\"sessionId\":\"chat-image-test\"}}";

    private static JsonElement ParseFrame(string line) => JsonDocument.Parse(line).RootElement.Clone();

    /// <summary>The stdout frames of a capture, without its first record (the sent commands), in order.</summary>
    private static IReadOnlyList<string> ReadCapturedFrames(string name) =>
        OmpRpcCapture.ReadFixture(name).Skip(1).ToArray();

    /// <summary>The commands the host sent in a capture, in order.</summary>
    private static IEnumerable<JsonElement> ReadCapturedRequests(string name)
    {
        var record = JsonDocument.Parse(OmpRpcCapture.ReadFixture(name)[0]).RootElement;
        return record.GetProperty("sent").EnumerateArray().Select(static frame => frame.Clone());
    }

    /// <summary>
    /// Plays OMP for one run: each process hands out its frames in order, whatever the host writes, and records the host's writes.
    /// </summary>
    private sealed class ReplayRpcProcess(IReadOnlyList<string> frames) : IOmpRpcProcess
    {
        private readonly Queue<string> _frames = new(frames);
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

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_frames.Count > 0 ? _frames.Dequeue() : null);
            }
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _written.Add(line);
            }

            return ValueTask.CompletedTask;
        }

        public void CloseInput()
        {
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hands out one process per RPC start, in order, and records the arguments of the JSON print run and of the retry.
    /// </summary>
    private sealed class ScriptedProvider(params IOmpRpcProcess[] processes) : OmpProvider(
        new StubExecutableResolver(),
        new CliProcessManager())
    {
        private readonly Queue<IOmpRpcProcess> _processes = new(processes);

        public int RpcStarts { get; private set; }

        public int PrintStarts { get; private set; }

        public string? RetryArguments { get; private set; }

        internal override ValueTask<IOmpRpcProcess> StartRpcProcessAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
        {
            RpcStarts++;
            if (RpcStarts > 1)
            {
                RetryArguments = string.Join(' ', startContext.Arguments);
            }

            return ValueTask.FromResult(_processes.Dequeue());
        }

        internal override Task<string?> ReadVersionAsync(ProcessStartContext startContext, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("18.14.1");

        internal override async IAsyncEnumerable<CliMessage> ExecuteProcessAsync(
            ProcessStartContext startContext,
            string? requestedSessionId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            PrintStarts++;
            await Task.CompletedTask;
            yield return new CliMessage("terminal.completed", JsonSerializer.SerializeToElement(new { type = "terminal.completed" }));
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
