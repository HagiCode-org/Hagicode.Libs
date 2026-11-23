using HagiCode.Libs.Providers.ClaudeCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>Process ledger ownership: the owner session id is kept apart from the Claude conversation id.</summary>
public sealed partial class ClaudeCodeProviderTests
{
    [Test]
    public async Task ExecuteAsync_attributes_the_process_to_the_owner_session_id_not_the_conversation_id()
    {
        var provider = CreateProvider();

        await CollectAsync(provider, new ClaudeCodeOptions
        {
            SessionId = "claude-conversation-id",
            OwnerSessionId = "core-session-id",
        });

        var ownership = provider.StartContexts.Single().Ownership.ShouldNotBeNull();
        ownership.SessionId.ShouldBe("core-session-id");
        ownership.ProviderName.ShouldBe("claude-code");
        provider.StartContexts.Single().Arguments.ShouldContain("--session-id", "claude-conversation-id");
        provider.StartContexts.Single().Arguments.ShouldNotContain("core-session-id");
    }

    [Test]
    public async Task ExecuteAsync_falls_back_to_the_conversation_id_when_no_owner_session_id_is_given()
    {
        var provider = CreateProvider();

        await CollectAsync(provider, new ClaudeCodeOptions { SessionId = "claude-conversation-id" });

        provider.StartContexts.Single().Ownership.ShouldNotBeNull().SessionId.ShouldBe("claude-conversation-id");
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task ExecuteAsync_treats_a_blank_owner_session_id_as_unset(string ownerSessionId)
    {
        var provider = CreateProvider();

        await CollectAsync(provider, new ClaudeCodeOptions
        {
            SessionId = "claude-conversation-id",
            OwnerSessionId = ownerSessionId,
        });

        provider.StartContexts.Single().Ownership.ShouldNotBeNull().SessionId.ShouldBe("claude-conversation-id");
    }
}
