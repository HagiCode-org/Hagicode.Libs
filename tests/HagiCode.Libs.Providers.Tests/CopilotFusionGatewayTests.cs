using GitHub.Copilot;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// The gateway tests share the process-wide Copilot environment lease coordinator, so they must not overlap.
[NonParallelizable]
public sealed class CopilotFusionGatewayTests
{
    [TestCase(null)]
    [TestCase(CopilotFusionTier.Standard)]
    [TestCase(CopilotFusionTier.High)]
    [TestCase(CopilotFusionTier.Max)]
    [Category("External")]
    public async Task Real_cli_applies_fusion_without_sending_a_prompt(CopilotFusionTier? tier)
    {
        ExternalGate.Require("HAGICODE_REAL_CLI_COPILOT_SESSION_TESTS");
        var executable = new CliExecutableResolver().ResolveFirstAvailablePath(["copilot"])
            ?? throw new InvalidOperationException("Copilot CLI was not found.");
        await using var provider = new CopilotProvider(new CliExecutableResolver(), new CliProcessManager());
        var preflight = await provider.CheckFusionCompatibilityAsync(new CopilotOptions
        {
            ExecutablePath = executable,
            Model = "fusion",
            FusionTier = tier
        });
        preflight.Status.ShouldBe(CopilotFusionPreflightStatus.Supported);

        var request = CreateRequest("fusion", tier) with
        {
            CliPath = executable,
            WorkingDirectory = Path.GetTempPath(),
            CliArgs = ["--no-custom-instructions", "--disable-builtin-mcps"],
            StartupTimeout = TimeSpan.FromSeconds(60)
        };
        var gateway = new GitHubCopilotSdkGateway();
        await using var runtime = await gateway.CreateRuntimeAsync(request);
        runtime.SessionId.ShouldNotBeNullOrWhiteSpace();
    }

    [TestCase(CopilotFusionTier.Standard)]
    [TestCase(CopilotFusionTier.High)]
    [TestCase(CopilotFusionTier.Max)]
    [TestCase(null)]
    public async Task CreateRuntime_maps_each_fusion_tier_into_native_create_configuration(CopilotFusionTier? tier)
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        await using var runtime = await gateway.CreateRuntimeAsync(CreateRequest("fusion", tier: tier));

        factory.CreateSessionCount.ShouldBe(1);
        factory.ResumeSessionCount.ShouldBe(0);
        factory.LastSessionConfig.ShouldNotBeNull().Model.ShouldBe("fusion");
#pragma warning disable GHCP001
        factory.LastSessionConfig!.Capi?.FusionTier?.Value.ShouldBe(tier?.ToString().ToLowerInvariant());
#pragma warning restore GHCP001
    }

    [TestCase(null)]
    [TestCase(CopilotFusionTier.Standard)]
    [TestCase(CopilotFusionTier.High)]
    [TestCase(CopilotFusionTier.Max)]
    public async Task CreateRuntime_maps_each_fusion_tier_into_native_resume_configuration(CopilotFusionTier? tier)
    {
        var factory = new CountingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        await using var runtime = await gateway.CreateRuntimeAsync(CreateRequest("fusion", tier: tier, sessionId: "restore-me"));

        factory.CreateSessionCount.ShouldBe(0);
        factory.ResumeSessionCount.ShouldBe(1);
        factory.LastResumeConfig.ShouldNotBeNull().Model.ShouldBe("fusion");
#pragma warning disable GHCP001
        factory.LastResumeConfig!.Capi?.FusionTier?.Value.ShouldBe(tier?.ToString().ToLowerInvariant());
#pragma warning restore GHCP001
    }

    [TestCase("auto", "standard", CopilotFusionTier.Standard)]
    [TestCase("auto", "high", CopilotFusionTier.High)]
    [TestCase("auto", "max", CopilotFusionTier.Max)]
    [TestCase("auto", "standard", null)]
    public void Runtime_verification_accepts_the_native_fusion_alias(string model, string tier, CopilotFusionTier? requestedTier)
    {
        Should.NotThrow(() => CopilotFusionCompatibility.EnsureRuntimeApplied("fusion", requestedTier, model, tier));
    }

    [TestCase("gpt-6-luna", null, null)]
    [TestCase("auto", null, null)]
    [TestCase("auto", "standard", CopilotFusionTier.Max)]
    public void Runtime_verification_rejects_model_fallback_unknown_tier_and_wrong_tier(
        string model, string? tier, CopilotFusionTier? requestedTier)
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            CopilotFusionCompatibility.EnsureRuntimeApplied("fusion", requestedTier, model, tier));
        exception.Message.ShouldContain("[copilot_fusion_unsupported]");
        exception.Message.ShouldContain("No prompt was sent.");
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

        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        await using var runtime = await gateway.CreateRuntimeAsync(CreateRequest("gpt-5") with
        {
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["HAGICODE_COPILOT_NATIVE_TEST_ENV"] = "configured",
                ["HAGICODE_COPILOT_NATIVE_UNSET_ENV"] = null
            }
        });

        factory.CreateSessionCount.ShouldBe(1);
        factory.LastSessionConfig.ShouldNotBeNull();
        factory.LastSessionConfig!.Model.ShouldBe("gpt-5");
        factory.LastSessionConfig.ReasoningEffort.ShouldBeNull();
        factory.LastSessionConfig.Capi.ShouldBeNull();
        var environment = factory.LastClientOptions.ShouldNotBeNull().Environment.ShouldNotBeNull();
        environment["PATH"].ShouldBe(inheritedPath);
        environment["HAGICODE_COPILOT_NATIVE_TEST_ENV"].ShouldBe("configured");
        environment.ContainsKey("HAGICODE_COPILOT_NATIVE_UNSET_ENV").ShouldBeFalse();
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

        public ResumeSessionConfig? LastResumeConfig { get; private set; }

        public CopilotClientOptions? LastClientOptions { get; private set; }

        public ICopilotSdkClient Create(CopilotClientOptions options)
        {
            ClientCreateCount++;
            LastClientOptions = options;
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
                owner.LastResumeConfig = config;
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
