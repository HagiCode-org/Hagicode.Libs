using GitHub.Copilot;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// The gateway tests share the process-wide Copilot environment lease coordinator, so they must not overlap.
[NonParallelizable]
public sealed class CopilotFusionRestoreTests
{
    [Test]
    public async Task Restore_compatibility_failure_is_terminal_and_creates_no_fresh_session()
    {
        var factory = new ResumeRefusingClientFactory();
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        // A single-model restore reaches ResumeSessionAsync, which reports a Fusion compatibility refusal.
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await gateway.CreateRuntimeAsync(CreateRestoreRequest()));

        exception.Message.ShouldContain("[copilot_fusion_unsupported]");
        factory.ResumeSessionCount.ShouldBe(1, "a deterministic compatibility refusal is not retried");
        factory.CreateSessionCount.ShouldBe(0, "no fresh session replaces the refused restore");
    }

    [Test]
    public async Task Restore_compatibility_failure_wrapped_in_another_exception_is_still_terminal()
    {
        var factory = new ResumeRefusingClientFactory(
            new InvalidOperationException("transport wrapper", new InvalidOperationException("[copilot_fusion_unsupported] wrapped refusal.")));
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await gateway.CreateRuntimeAsync(CreateRestoreRequest()));

        exception.Message.ShouldBe("transport wrapper");
        factory.ResumeSessionCount.ShouldBe(1);
        factory.CreateSessionCount.ShouldBe(0);
    }

    private static CopilotSdkRequest CreateRestoreRequest()
    {
        return new CopilotSdkRequest(
            Prompt: "do not send this prompt",
            Model: "gpt-5-mini",
            WorkingDirectory: null,
            SessionId: "restore-me",
            CliPath: "/resolved/copilot",
            CliUrl: null,
            GitHubToken: null,
            UseLoggedInUser: true,
            Timeout: TimeSpan.FromSeconds(30),
            IdleTimeout: TimeSpan.FromSeconds(30),
            StartupTimeout: TimeSpan.FromSeconds(30),
            CliArgs: [],
            EnvironmentVariables: new Dictionary<string, string?>());
    }

    private sealed class ResumeRefusingClientFactory(Exception? refusal = null) : ICopilotSdkClientFactory
    {
        private readonly Exception _refusal = refusal
                                              ?? new InvalidOperationException(
                                                  "[copilot_fusion_unsupported] Fusion request model='fusion' refused on resume.");

        public int CreateSessionCount { get; private set; }

        public int ResumeSessionCount { get; private set; }

        public ICopilotSdkClient Create(CopilotClientOptions options) => new RefusingClient(this);

        private sealed class RefusingClient(ResumeRefusingClientFactory owner) : ICopilotSdkClient
        {
            public Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
            {
                owner.CreateSessionCount++;
                throw new InvalidOperationException("A fresh session must not be created after a terminal compatibility refusal.");
            }

            public Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
            {
                owner.ResumeSessionCount++;
                throw owner._refusal;
            }

            public Task<IReadOnlyList<CopilotSdkModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<CopilotSdkModelInfo>>([]);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
