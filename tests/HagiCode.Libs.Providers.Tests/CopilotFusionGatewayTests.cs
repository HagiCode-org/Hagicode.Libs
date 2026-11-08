using GitHub.Copilot;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// The gateway tests share the process-wide Copilot environment lease coordinator, so they must not overlap.
[NonParallelizable]
public sealed class CopilotFusionGatewayTests
{
    [TestCase(CopilotFusionTier.Standard)]
    [TestCase(CopilotFusionTier.High)]
    [TestCase(CopilotFusionTier.Max)]
    public async Task CreateRuntime_rejects_each_fusion_tier_before_any_client_or_session_operation(CopilotFusionTier tier)
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await gateway.CreateRuntimeAsync(CreateRequest("fusion", tier: tier)));

        exception.Message.ShouldContain("[copilot_fusion_unsupported]");
        exception.Message.ShouldContain($"requested tier='{tier.ToString().ToLowerInvariant()}'");
        exception.Message.ShouldContain("/resolved/copilot");
        exception.Message.ShouldContain(CopilotFusionCompatibility.GetSdkVersion()!);
        exception.Message.ShouldContain("No session was created or restored, and no prompt was sent.");
        factory.ClientCreateCount.ShouldBe(0);
        factory.CreateSessionCount.ShouldBe(0);
        factory.ResumeSessionCount.ShouldBe(0);
    }

    [Test]
    public async Task SendPrompt_rejects_fusion_tier_before_any_client_or_prompt_send()
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in gateway.SendPromptAsync(CreateRequest("fusion", tier: CopilotFusionTier.Max, sessionId: "restore-me")))
            {
            }
        });

        exception.Message.ShouldContain("requested tier='max'");
        factory.ClientCreateCount.ShouldBe(0);
        factory.ResumeSessionCount.ShouldBe(0);
    }

    [Test]
    public async Task CreateRuntime_rejects_unspecified_fusion_tier_as_unverified_default()
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await gateway.CreateRuntimeAsync(CreateRequest("fusion")));

        exception.Message.ShouldContain("requested tier='unspecified'");
        factory.ClientCreateCount.ShouldBe(0);
        factory.CreateSessionCount.ShouldBe(0);
    }

    [Test]
    public async Task CreateRuntime_rejects_explicit_fusion_tier_reset_before_any_session_operation()
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await gateway.CreateRuntimeAsync(CreateRequest("fusion", explicitlyUnset: true, sessionId: "restore-me")));

        exception.Message.ShouldContain("explicit Fusion tier reset");
        factory.ClientCreateCount.ShouldBe(0);
        factory.ResumeSessionCount.ShouldBe(0);
    }

    [TestCase(42)]
    [TestCase(0)]
    public async Task CreateRuntime_rejects_undefined_fusion_tier_as_invalid_input(int value)
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await gateway.CreateRuntimeAsync(CreateRequest("fusion", tier: (CopilotFusionTier)value)));

        exception.Message.ShouldContain("[copilot_fusion_invalid]");
        factory.ClientCreateCount.ShouldBe(0);
    }

    [Test]
    public async Task CreateRuntime_rejects_concrete_fusion_tier_with_a_single_model_as_invalid_input()
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await gateway.CreateRuntimeAsync(CreateRequest("gpt-5", tier: CopilotFusionTier.High)));

        exception.Message.ShouldContain("[copilot_fusion_invalid]");
        factory.ClientCreateCount.ShouldBe(0);
    }

    [Test]
    public async Task CreateRuntime_single_model_without_fusion_intent_sends_no_fusion_configuration()
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        await using var runtime = await gateway.CreateRuntimeAsync(CreateRequest("gpt-5"));

        factory.CreateSessionCount.ShouldBe(1);
        factory.LastSessionConfig.ShouldNotBeNull();
        factory.LastSessionConfig!.Model.ShouldBe("gpt-5");
        factory.LastSessionConfig.ReasoningEffort.ShouldBeNull();
    }

    private static CopilotSdkRequest CreateRequest(
        string? model,
        CopilotFusionTier? tier = null,
        bool explicitlyUnset = false,
        string? sessionId = null)
    {
        return new CopilotSdkRequest(
            Prompt: "do not send this prompt",
            Model: model,
            WorkingDirectory: null,
            SessionId: sessionId,
            CliPath: "/resolved/copilot",
            CliUrl: null,
            GitHubToken: null,
            UseLoggedInUser: true,
            Timeout: TimeSpan.FromSeconds(30),
            IdleTimeout: TimeSpan.FromSeconds(30),
            StartupTimeout: TimeSpan.FromSeconds(30),
            CliArgs: [],
            EnvironmentVariables: new Dictionary<string, string?>(),
            FusionTier: tier,
            FusionTierIsExplicitlyUnset: explicitlyUnset);
    }

    private sealed class CountingClientFactory : ICopilotSdkClientFactory
    {
        public int ClientCreateCount { get; private set; }

        public int CreateSessionCount { get; private set; }

        public int ResumeSessionCount { get; private set; }

        public SessionConfig? LastSessionConfig { get; private set; }

        public ICopilotSdkClient Create(CopilotClientOptions options)
        {
            ClientCreateCount++;
            return new CountingClient(this);
        }

        private sealed class CountingClient(CountingClientFactory owner) : ICopilotSdkClient
        {
            public Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
            {
                owner.CreateSessionCount++;
                owner.LastSessionConfig = config;
                return Task.FromResult<ICopilotSdkSession>(new CountingSession(config.SessionId ?? "fresh-session"));
            }

            public Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
            {
                owner.ResumeSessionCount++;
                return Task.FromResult<ICopilotSdkSession>(new CountingSession(sessionId));
            }

            public Task<IReadOnlyList<CopilotSdkModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<CopilotSdkModelInfo>>([]);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class CountingSession(string sessionId) : ICopilotSdkSession
        {
            public string SessionId => sessionId;

            public IDisposable On(Action<SessionEvent> handler) => new NoopDisposable();

            public Task SendAsync(MessageOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
