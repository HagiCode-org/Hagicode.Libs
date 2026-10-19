using System.Text.Json;
using GitHub.Copilot;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Copilot requests rewrite process-wide environment variables, so these classes must not overlap with other Copilot tests.
[NonParallelizable]
public sealed class CopilotImageAttachmentTests
{
    // A 1x1 PNG. Its base64 form must never appear in any diagnostic, raw event or exception message.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static readonly string PngBase64 = Convert.ToBase64String(PngBytes);

    [Test]
    public void Default_options_carry_no_images()
    {
        new CopilotOptions().Images.ShouldBeEmpty();
    }

    [Test]
    public void Image_string_json_and_options_string_never_contain_the_bytes()
    {
        var image = new CopilotImageAttachment("image/png", PngBytes, "diagram.png");

        var imageText = image.ToString();
        var imageJson = JsonSerializer.Serialize(image);
        var optionsText = new CopilotOptions { Images = [image] }.ToString();

        imageText.ShouldContain("image/png");
        imageText.ShouldContain("diagram.png");
        imageText.ShouldContain($"ByteLength = {PngBytes.Length}");
        imageText.ShouldNotContain(PngBase64);
        imageJson.ShouldNotContain(PngBase64);
        imageJson.ShouldNotContain("Data");
        optionsText.ShouldContain("Images = 1");
        optionsText.ShouldNotContain(PngBase64);
    }

    [Test]
    public void BuildSdkRequest_carries_images_and_text_only_request_carries_none()
    {
        var provider = CreateProvider(new RecordingGateway());
        var image = new CopilotImageAttachment("image/png", PngBytes, "a.png");

        var withImage = provider.BuildSdkRequest(
            new CopilotOptions { ExecutablePath = "/custom/copilot", Images = [image] },
            "describe",
            "/custom/copilot",
            new Dictionary<string, string?>());
        var textOnly = provider.BuildSdkRequest(
            new CopilotOptions { ExecutablePath = "/custom/copilot" },
            "describe",
            "/custom/copilot",
            new Dictionary<string, string?>());

        withImage.Images.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBeSameAs(image);
        textOnly.Images!.ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_rejects_invalid_images_before_any_sdk_call()
    {
        var gateway = new RecordingGateway();
        var provider = CreateProvider(gateway);
        var cases = new (CopilotImageAttachment? Image, string Expected)[]
        {
            (null, "Images[0] is null."),
            (new CopilotImageAttachment("image/png", ReadOnlyMemory<byte>.Empty), "Images[0] has no data."),
            (new CopilotImageAttachment("application/pdf", PngBytes), "application/pdf"),
            (new CopilotImageAttachment(string.Empty, PngBytes), "Images[0]")
        };

        foreach (var (image, expected) in cases)
        {
            var error = await Should.ThrowAsync<InvalidOperationException>(
                () => ReadAllAsync(provider, new CopilotOptions { ExecutablePath = "/custom/copilot", Images = [image!] }));

            error.Message.ShouldContain(expected);
            error.Message.ShouldNotContain(PngBase64);
        }

        gateway.SdkCallCount.ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_text_only_send_carries_no_attachment()
    {
        var factory = new RecordingClientFactory();
        var provider = CreateProvider(new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator()));

        await ReadAllAsync(provider, new CopilotOptions { ExecutablePath = "/custom/copilot" });

        factory.SentMessages.ShouldHaveSingleItem().Attachments.ShouldBeNull();
    }

    [Test]
    public async Task ExecuteAsync_fresh_session_sends_each_image_once_as_an_inline_blob_in_order()
    {
        var factory = new RecordingClientFactory();
        var provider = CreateProvider(new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator()));

        await ReadAllAsync(provider, new CopilotOptions
        {
            ExecutablePath = "/custom/copilot",
            Images =
            [
                new CopilotImageAttachment("image/png", PngBytes, "first.png"),
                new CopilotImageAttachment("image/jpeg", PngBytes)
            ]
        });

        var message = factory.SentMessages.ShouldHaveSingleItem();
        message.Prompt.ShouldBe("describe");
        var attachments = message.Attachments.ShouldNotBeNull();
        attachments.Count.ShouldBe(2);

        var first = attachments[0].ShouldBeOfType<UserMessageAttachmentBlob>();
        first.Data.ShouldBe(PngBase64);
        first.MimeType.ShouldBe("image/png");
        first.DisplayName.ShouldBe("first.png");

        var second = attachments[1].ShouldBeOfType<UserMessageAttachmentBlob>();
        second.MimeType.ShouldBe("image/jpeg");
        second.DisplayName.ShouldBe("image");
        factory.CreateSessionCount.ShouldBe(1);
        factory.ResumeSessionCount.ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_successful_resume_sends_images_once_after_the_resume()
    {
        var factory = new RecordingClientFactory();
        var provider = CreateProvider(new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator()));

        await ReadAllAsync(provider, new CopilotOptions
        {
            ExecutablePath = "/custom/copilot",
            SessionId = "resume-me",
            Images = [new CopilotImageAttachment("image/png", PngBytes, "a.png")]
        });

        factory.ResumeSessionCount.ShouldBe(1);
        factory.CreateSessionCount.ShouldBe(0);
        factory.SentMessages.ShouldHaveSingleItem().Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
    }

    [Test]
    public async Task ExecuteAsync_failed_resume_falls_back_to_one_fresh_send_with_images_once()
    {
        var factory = new RecordingClientFactory { ResumeFails = true };
        var provider = CreateProvider(new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator()));

        await ReadAllAsync(provider, new CopilotOptions
        {
            ExecutablePath = "/custom/copilot",
            SessionId = "resume-me",
            Images = [new CopilotImageAttachment("image/png", PngBytes, "a.png")]
        });

        factory.ResumeSessionCount.ShouldBeGreaterThan(0);
        factory.CreateSessionCount.ShouldBe(1);
        factory.SentMessages.ShouldHaveSingleItem().Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
    }

    [Test]
    public void DispatchSessionEvent_redacts_blob_data_from_an_echoed_user_message()
    {
        var echo = new UserMessageEvent
        {
            Data = new UserMessageData
            {
                Content = "see image",
                Attachments =
                [
                    new UserMessageAttachmentBlob
                    {
                        Data = PngBase64,
                        MimeType = "image/png",
                        DisplayName = "a.png"
                    }
                ]
            }
        };

        var result = GitHubCopilotSdkGateway.DispatchSessionEvent(echo, sawDelta: false);

        var raw = result.Events.ShouldHaveSingleItem();
        raw.Type.ShouldBe(CopilotSdkStreamEventType.RawEvent);
        raw.RawEventType.ShouldBe(nameof(UserMessageEvent));
        raw.Content.ShouldNotBeNull();
        raw.Content.ShouldNotContain(PngBase64);
        raw.Content.ShouldContain("image/png");
        raw.Content.ShouldContain("redacted base64 image data");
    }

    [Test]
    public void DispatchSessionEvent_serializes_an_event_without_attachments_as_before()
    {
        var plain = new UserMessageEvent
        {
            Data = new UserMessageData { Content = "plain text" }
        };

        var raw = GitHubCopilotSdkGateway.DispatchSessionEvent(plain, sawDelta: false).Events.ShouldHaveSingleItem();

        raw.Content.ShouldBe(JsonSerializer.Serialize<object>(plain));
    }

    [Test]
    public void DispatchSessionEvent_falls_back_to_the_string_form_when_serialization_throws()
    {
        var unserializable = new UnserializableSessionEvent();

        var raw = GitHubCopilotSdkGateway.DispatchSessionEvent(unserializable, sawDelta: false).Events.ShouldHaveSingleItem();

        raw.Content.ShouldBe(unserializable.ToString());
    }

    [Test]
    public async Task GetImageCapabilityAsync_reports_vision_limits_the_model_reports()
    {
        var gateway = new RecordingGateway(
        [
            new CopilotSdkModelInfo("vision-model", true, 3, 5_000_000, ["image/png", "image/jpeg"])
        ]);
        var provider = CreateProvider(gateway);

        var capability = await provider.GetImageCapabilityAsync(
            new CopilotOptions { ExecutablePath = "/custom/copilot" },
            "vision-model");

        capability.ShouldNotBeNull();
        capability.SupportsVision.ShouldBeTrue();
        capability.MaxPromptImages.ShouldBe(3);
        capability.MaxPromptImageSize.ShouldBe(5_000_000);
        capability.SupportedMediaTypes.ShouldBe(["image/png", "image/jpeg"]);
    }

    [Test]
    public async Task GetImageCapabilityAsync_reports_a_text_only_model_as_without_vision()
    {
        var provider = CreateProvider(new RecordingGateway(
        [
            new CopilotSdkModelInfo("text-model", false, null, null, null)
        ]));

        var capability = await provider.GetImageCapabilityAsync(
            new CopilotOptions { ExecutablePath = "/custom/copilot" },
            "text-model");

        capability.ShouldNotBeNull();
        capability.SupportsVision.ShouldBeFalse();
        capability.MaxPromptImages.ShouldBeNull();
        capability.MaxPromptImageSize.ShouldBeNull();
        capability.SupportedMediaTypes.ShouldBeNull();
    }

    [Test]
    public async Task GetImageCapabilityAsync_leaves_omitted_or_zero_limits_absent()
    {
        var provider = CreateProvider(new RecordingGateway(
        [
            new CopilotSdkModelInfo("vision-no-limits", true, null, 0, [])
        ]));

        var capability = await provider.GetImageCapabilityAsync(
            new CopilotOptions { ExecutablePath = "/custom/copilot" },
            "vision-no-limits");

        capability.ShouldNotBeNull();
        capability.SupportsVision.ShouldBeTrue();
        capability.MaxPromptImages.ShouldBeNull();
        capability.MaxPromptImageSize.ShouldBeNull();
        capability.SupportedMediaTypes.ShouldBeNull();
    }

    [Test]
    public async Task GetImageCapabilityAsync_returns_unknown_for_a_model_that_is_not_listed_or_has_no_vision_entry()
    {
        var provider = CreateProvider(new RecordingGateway(
        [
            new CopilotSdkModelInfo("no-vision-entry", null, null, null, null)
        ]));
        var options = new CopilotOptions { ExecutablePath = "/custom/copilot" };

        (await provider.GetImageCapabilityAsync(options, "unlisted-model")).ShouldBeNull();
        (await provider.GetImageCapabilityAsync(options, "no-vision-entry")).ShouldBeNull();
    }

    [Test]
    public async Task GetImageCapabilityAsync_without_a_model_returns_unknown_and_starts_no_client()
    {
        var gateway = new RecordingGateway();
        var provider = CreateProvider(gateway);

        (await provider.GetImageCapabilityAsync(new CopilotOptions { ExecutablePath = "/custom/copilot" }, null)).ShouldBeNull();

        gateway.SdkCallCount.ShouldBe(0);
    }

    [Test]
    public async Task GetImageCapabilityAsync_returns_unknown_when_the_list_fails()
    {
        var provider = CreateProvider(new RecordingGateway(listFailure: new InvalidOperationException("not authenticated")));

        (await provider.GetImageCapabilityAsync(new CopilotOptions { ExecutablePath = "/custom/copilot" }, "vision-model"))
            .ShouldBeNull();
    }

    [Test]
    public async Task ListModelsAsync_disposes_the_client_when_the_list_fails()
    {
        var factory = new RecordingClientFactory { ListModelsFails = true };
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());
        var provider = CreateProvider(gateway);
        var request = provider.BuildSdkRequest(
            new CopilotOptions { ExecutablePath = "/custom/copilot", Model = "vision-model" },
            "list models",
            "/custom/copilot",
            new Dictionary<string, string?>());

        await Should.ThrowAsync<InvalidOperationException>(() => gateway.ListModelsAsync(request));

        factory.DisposedClientCount.ShouldBe(1);
    }

    [Test]
    public async Task ListModelsAsync_returns_the_models_and_disposes_the_client()
    {
        var factory = new RecordingClientFactory
        {
            Models = [new CopilotSdkModelInfo("vision-model", true, 2, 1_000, ["image/png"])]
        };
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());
        var provider = CreateProvider(gateway);
        var request = provider.BuildSdkRequest(
            new CopilotOptions { ExecutablePath = "/custom/copilot", Model = "vision-model" },
            "list models",
            "/custom/copilot",
            new Dictionary<string, string?>());

        var models = await gateway.ListModelsAsync(request);

        models.ShouldHaveSingleItem().Id.ShouldBe("vision-model");
        factory.DisposedClientCount.ShouldBe(1);
    }

    private static CopilotProvider CreateProvider(ICopilotSdkGateway gateway)
    {
        return new CopilotProvider(new StubExecutableResolver(), new CliProcessManager(), gateway, new EmptyRuntimeEnvironmentResolver());
    }

    private static async Task ReadAllAsync(CopilotProvider provider, CopilotOptions options)
    {
        await foreach (var _ in provider.ExecuteAsync(options, "describe"))
        {
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override IReadOnlyList<string> ResolveExecutablePaths(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => string.IsNullOrWhiteSpace(executableName) ? [] : [executableName];
    }

    private sealed class EmptyRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
    }

    // Counts every SDK call and returns a fixed model list, so provider-level tests never reach a client.
    private sealed class RecordingGateway(
        IReadOnlyList<CopilotSdkModelInfo>? models = null,
        Exception? listFailure = null) : ICopilotSdkGateway
    {
        public int SdkCallCount { get; private set; }

        public Task<ICopilotSdkRuntime> CreateRuntimeAsync(CopilotSdkRequest request, CancellationToken cancellationToken = default)
        {
            SdkCallCount++;
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
            CopilotSdkRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SdkCallCount++;
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<CopilotSdkModelInfo>> ListModelsAsync(CopilotSdkRequest request, CancellationToken cancellationToken = default)
        {
            SdkCallCount++;
            if (listFailure is not null)
            {
                return Task.FromException<IReadOnlyList<CopilotSdkModelInfo>>(listFailure);
            }

            return Task.FromResult(models ?? []);
        }
    }

    // Records each message sent on the session, and counts session creation, resume and client disposal.
    private sealed class RecordingClientFactory : ICopilotSdkClientFactory
    {
        public List<MessageOptions> SentMessages { get; } = [];

        public int CreateSessionCount { get; private set; }

        public int ResumeSessionCount { get; private set; }

        public int DisposedClientCount { get; private set; }

        public bool ResumeFails { get; init; }

        public bool ListModelsFails { get; init; }

        public IReadOnlyList<CopilotSdkModelInfo> Models { get; init; } = [];

        public ICopilotSdkClient Create(CopilotClientOptions options) => new RecordingClient(this);

        private sealed class RecordingClient(RecordingClientFactory owner) : ICopilotSdkClient
        {
            public Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
            {
                owner.CreateSessionCount++;
                return Task.FromResult<ICopilotSdkSession>(new RecordingSession(config.SessionId ?? "fresh-session", owner));
            }

            public Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
            {
                owner.ResumeSessionCount++;
                if (owner.ResumeFails)
                {
                    return Task.FromException<ICopilotSdkSession>(
                        new InvalidOperationException("Request session.resume failed with message: session not found"));
                }

                return Task.FromResult<ICopilotSdkSession>(new RecordingSession(sessionId, owner));
            }

            public Task<IReadOnlyList<CopilotSdkModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            {
                if (owner.ListModelsFails)
                {
                    return Task.FromException<IReadOnlyList<CopilotSdkModelInfo>>(new InvalidOperationException("not authenticated"));
                }

                return Task.FromResult(owner.Models);
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposedClientCount++;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class RecordingSession(string sessionId, RecordingClientFactory owner) : ICopilotSdkSession
        {
            public string SessionId => sessionId;

            public IDisposable On(Action<SessionEvent> handler) => new NoopDisposable();

            public Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken)
            {
                owner.SentMessages.Add(options);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    // A session event that System.Text.Json cannot serialize, to exercise the fallback to the string form.
    private sealed class UnserializableSessionEvent : SessionEvent
    {
        public Func<int> Callback { get; init; } = () => 1;
    }
}
