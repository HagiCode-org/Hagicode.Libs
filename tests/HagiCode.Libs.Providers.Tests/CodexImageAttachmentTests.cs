using System.Reflection;
using System.Text.Json;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Configuration;
using ManagedCode.CodexSharpSDK.Execution;
using ManagedCode.CodexSharpSDK.Models;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexImageAttachmentTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] SvgBytes = [0x3C, 0x73, 0x76, 0x67];

    [Test]
    public void ToString_shows_media_type_and_length_but_never_the_bytes()
    {
        var attachment = new CodexImageAttachment("image/png", PngBytes);

        var text = attachment.ToString();

        text.ShouldContain("image/png");
        text.ShouldContain("ByteLength = 8");
        text.ShouldNotContain(Convert.ToBase64String(PngBytes));
    }

    [Test]
    public void Json_serialization_omits_the_bytes()
    {
        var json = JsonSerializer.Serialize(new CodexImageAttachment("image/png", PngBytes));

        json.ShouldBe("{\"MediaType\":\"image/png\"}");
        json.ShouldNotContain(Convert.ToBase64String(PngBytes));
    }

    [Test]
    public void Supported_media_types_match_ignoring_case_and_reject_everything_else()
    {
        CodexImageAttachment.IsSupportedMediaType("image/png").ShouldBeTrue();
        CodexImageAttachment.IsSupportedMediaType("IMAGE/JPEG").ShouldBeTrue();
        CodexImageAttachment.IsSupportedMediaType("image/gif").ShouldBeTrue();
        CodexImageAttachment.IsSupportedMediaType("image/webp").ShouldBeTrue();
        CodexImageAttachment.IsSupportedMediaType("image/svg+xml").ShouldBeFalse();
        CodexImageAttachment.IsSupportedMediaType(null).ShouldBeFalse();
    }

    [Test]
    public void Turn_input_without_attachments_is_the_text_block_alone()
    {
        var input = CodexAppServerSession.BuildTurnInput("hello", []);

        input.ToJsonString().ShouldBe("[{\"type\":\"text\",\"text\":\"hello\",\"text_elements\":[]}]");
    }

    [Test]
    public void Turn_input_appends_image_data_url_blocks_after_the_text_block_in_order()
    {
        var input = CodexAppServerSession.BuildTurnInput(
            "look at these",
            [new CodexImageAttachment("image/png", PngBytes), new CodexImageAttachment("image/jpeg", JpegBytes)]);

        input.Count.ShouldBe(3);
        input[0]!["type"]!.GetValue<string>().ShouldBe("text");
        input[0]!["text"]!.GetValue<string>().ShouldBe("look at these");
        input[1]!["type"]!.GetValue<string>().ShouldBe("image");
        input[1]!["url"]!.GetValue<string>().ShouldBe("data:image/png;base64," + Convert.ToBase64String(PngBytes));
        input[2]!["type"]!.GetValue<string>().ShouldBe("image");
        input[2]!["url"]!.GetValue<string>().ShouldStartWith("data:image/jpeg;base64,");
    }

    [Test]
    public void Image_blocks_do_not_set_a_detail_level()
    {
        var input = CodexAppServerSession.BuildTurnInput("q", [new CodexImageAttachment("image/png", PngBytes)]);

        input[1]!.AsObject().ContainsKey("detail").ShouldBeFalse();
    }

    [Test]
    public void Data_url_payload_decodes_back_to_the_original_bytes()
    {
        var url = CodexAppServerSession.BuildDataUrl(new CodexImageAttachment("image/png", PngBytes));

        Convert.FromBase64String(url["data:image/png;base64,".Length..]).ShouldBe(PngBytes);
    }

    [Test]
    public void Attachments_are_not_a_property_of_the_reusable_session_options()
    {
        // Image bytes must never reach the pooled-entry compatibility fingerprint, so they stay on the per-turn call.
        var carriesImages = typeof(CodexSessionOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.PropertyType == typeof(CodexImageAttachment)
                || (property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericArguments().Contains(typeof(CodexImageAttachment))));

        carriesImages.ShouldBeFalse();
    }

    [Test]
    public async Task Exec_turn_passes_images_after_the_flags_writes_them_outside_the_workspace_and_removes_them()
    {
        var runner = new ImageCapturingProcessRunner();
        using var client = CreateExecClient(runner);
        var thread = client.ResumeThread("thread-123", new ThreadOptions());

        var result = await thread.RunStreamedAsync(
            "describe the pictures",
            [new CodexImageAttachment("image/png", PngBytes), new CodexImageAttachment("image/jpeg", JpegBytes)]);
        await Drain(result.Events);

        var invocation = runner.Invocations.Single();
        invocation.Input.ShouldBe("describe the pictures");
        invocation.Arguments.ShouldNotContain("describe the pictures");

        var imageArguments = invocation.Arguments.Skip(invocation.Arguments.Count - 4).ToArray();
        imageArguments[0].ShouldBe("--image");
        imageArguments[2].ShouldBe("--image");
        invocation.Arguments.Take(invocation.Arguments.Count - 4).ShouldContain("resume");

        runner.SeenImages.Count.ShouldBe(2);
        runner.SeenImages[0].Path.ShouldBe(imageArguments[1]);
        runner.SeenImages[0].Bytes.ShouldBe(PngBytes);
        runner.SeenImages[1].Path.ShouldBe(imageArguments[3]);
        runner.SeenImages[1].Bytes.ShouldBe(JpegBytes);
        runner.SeenImages[0].Path.ShouldEndWith(".png");
        runner.SeenImages[1].Path.ShouldEndWith(".jpg");
        runner.SeenImages.ShouldAllBe(seen => IsOutsideWorkingDirectory(seen.Path));

        File.Exists(runner.SeenImages[0].Path).ShouldBeFalse();
        File.Exists(runner.SeenImages[1].Path).ShouldBeFalse();
    }

    [Test]
    public async Task Exec_temporary_images_are_removed_when_the_process_fails()
    {
        var runner = new ImageCapturingProcessRunner { FailAfterCapture = true };
        using var client = CreateExecClient(runner);
        var thread = client.StartThread(new ThreadOptions());

        var result = await thread.RunStreamedAsync("describe", [new CodexImageAttachment("image/png", PngBytes)]);
        await Should.ThrowAsync<InvalidOperationException>(() => Drain(result.Events));

        runner.SeenImages.Count.ShouldBe(1);
        File.Exists(runner.SeenImages[0].Path).ShouldBeFalse();
    }

    [Test]
    public async Task Exec_temporary_images_are_removed_when_the_run_is_cancelled()
    {
        var runner = new ImageCapturingProcessRunner { WaitForCancellation = true };
        using var client = CreateExecClient(runner);
        var thread = client.StartThread(new ThreadOptions());
        using var cancellation = new CancellationTokenSource();

        var result = await thread.RunStreamedAsync(
            "describe",
            [new CodexImageAttachment("image/png", PngBytes)],
            new TurnOptions { CancellationToken = cancellation.Token });
        var consumption = Drain(result.Events);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => consumption);

        runner.SeenImages.Count.ShouldBe(1);
        File.Exists(runner.SeenImages[0].Path).ShouldBeFalse();
    }

    [Test]
    public async Task Unsupported_media_type_is_rejected_before_any_process_starts()
    {
        var runner = new ImageCapturingProcessRunner();
        using var client = CreateExecClient(runner);
        var thread = client.StartThread(new ThreadOptions());

        var exception = Should.Throw<ArgumentException>(() => thread.RunStreamedAsync(
            "describe",
            [new CodexImageAttachment("image/svg+xml", SvgBytes)]));

        exception.Message.ShouldContain("image/svg+xml");
        exception.Message.ShouldNotContain(Convert.ToBase64String(SvgBytes));
        runner.Invocations.ShouldBeEmpty();
    }

    private static bool IsOutsideWorkingDirectory(string path)
    {
        var workingDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        return !Path.GetFullPath(path).StartsWith(workingDirectory, StringComparison.Ordinal);
    }

    private static CodexClient CreateExecClient(ICodexProcessRunner runner) =>
        new(
            new CodexClientOptions
            {
                AutoStart = true,
                CodexOptions = new CodexOptions { CodexExecutablePath = "/tmp/codex" }
            },
            new CodexExec(
                executablePath: "/tmp/codex",
                environmentOverride: new Dictionary<string, string>(),
                configOverrides: null,
                processRunner: runner));

    private static async Task Drain(IAsyncEnumerable<ThreadEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private sealed record SeenImage(string Path, byte[] Bytes);

    /// <summary>Records each <c>--image</c> file while it exists, then ends the run the way the scripted tests do.</summary>
    private sealed class ImageCapturingProcessRunner : ICodexProcessRunner
    {
        public List<CodexProcessInvocation> Invocations { get; } = [];

        public List<SeenImage> SeenImages { get; } = [];

        public bool FailAfterCapture { get; init; }

        public bool WaitForCancellation { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<string> RunAsync(
            CodexProcessInvocation invocation,
            Microsoft.Extensions.Logging.ILogger logger,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            for (var index = 0; index < invocation.Arguments.Count - 1; index++)
            {
                if (invocation.Arguments[index] == "--image")
                {
                    var path = invocation.Arguments[index + 1];
                    SeenImages.Add(new SeenImage(path, await File.ReadAllBytesAsync(path, cancellationToken)));
                }
            }

            if (FailAfterCapture)
            {
                throw new InvalidOperationException("codex exited with status 1");
            }

            if (WaitForCancellation)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            yield return "{\"type\":\"item.completed\",\"item\":{\"id\":\"msg-1\",\"type\":\"agent_message\",\"text\":\"ok\"}}";
            yield return "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1}}";
        }
    }
}
