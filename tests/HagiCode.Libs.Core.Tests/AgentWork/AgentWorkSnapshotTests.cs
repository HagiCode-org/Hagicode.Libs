using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Core.Tests.AgentWork;

public sealed class AgentWorkSnapshotTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToCliMessage_uses_camel_case_and_omits_absent_members()
    {
        var snapshot = new AgentWorkSnapshot(
            3,
            [
                new AgentWorkItem(
                    "claude:t1",
                    AgentWorkKinds.Monitor,
                    null,
                    AgentWorkStatuses.Running,
                    "Watch CI",
                    "gh run watch",
                    Start,
                    null,
                    Start.AddMinutes(5),
                    null,
                    null,
                    "toolu_1",
                    true)
            ]);

        var message = snapshot.ToCliMessage();

        message.Type.ShouldBe(AgentWorkSnapshot.MessageType);
        message.Type.ShouldBe("agent_work_snapshot");
        message.Content.GetProperty("sequence").GetInt64().ShouldBe(3);
        var item = message.Content.GetProperty("items")[0];
        item.GetProperty("id").GetString().ShouldBe("claude:t1");
        item.GetProperty("blocksCompletion").GetBoolean().ShouldBeTrue();
        item.GetProperty("deadlineAt").GetDateTimeOffset().ShouldBe(Start.AddMinutes(5));
        item.TryGetProperty("parentId", out _).ShouldBeFalse();
        item.TryGetProperty("endedAt", out _).ShouldBeFalse();
        item.TryGetProperty("summary", out _).ShouldBeFalse();
        item.TryGetProperty("usage", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryFromCliMessage_round_trips_a_snapshot_with_usage_and_an_unknown_kind()
    {
        var snapshot = new AgentWorkSnapshot(
            1,
            [
                new AgentWorkItem(
                    "codex:7",
                    "something-new",
                    "codex:1",
                    AgentWorkStatuses.Completed,
                    string.Empty,
                    null,
                    Start,
                    Start.AddSeconds(48),
                    null,
                    "done",
                    new AgentWorkUsage(TotalTokens: 12473, ToolUses: 2, DurationMs: 48_000),
                    null,
                    false)
            ]);

        AgentWorkSnapshot.TryFromCliMessage(snapshot.ToCliMessage(), out var restored).ShouldBeTrue();

        restored.ShouldBe(snapshot, new AgentWorkSnapshotComparer());
    }

    [Fact]
    public void TryFromCliMessage_rejects_other_message_types_and_malformed_content()
    {
        var notSnapshot = new CliMessage("assistant", JsonSerializer.SerializeToElement(new { sequence = 1, items = Array.Empty<object>() }));
        var malformed = new CliMessage(AgentWorkSnapshot.MessageType, JsonSerializer.SerializeToElement(new { sequence = "x", items = 5 }));
        var notObject = new CliMessage(AgentWorkSnapshot.MessageType, JsonSerializer.SerializeToElement("text"));

        AgentWorkSnapshot.TryFromCliMessage(notSnapshot, out _).ShouldBeFalse();
        AgentWorkSnapshot.TryFromCliMessage(malformed, out _).ShouldBeFalse();
        AgentWorkSnapshot.TryFromCliMessage(notObject, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("starting", true)]
    [InlineData("running", true)]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData("stopped", false)]
    [InlineData("whatever", false)]
    [InlineData(null, false)]
    public void IsActive_is_true_only_for_starting_and_running(string? status, bool expected)
    {
        AgentWorkStatuses.IsActive(status).ShouldBe(expected);
    }

    // Records compare their item lists by reference, so snapshots are compared by their wire form.
    private sealed class AgentWorkSnapshotComparer : IEqualityComparer<AgentWorkSnapshot>
    {
        public bool Equals(AgentWorkSnapshot? x, AgentWorkSnapshot? y) =>
            JsonSerializer.Serialize(x, AgentWorkSnapshot.JsonOptions) == JsonSerializer.Serialize(y, AgentWorkSnapshot.JsonOptions);

        public int GetHashCode(AgentWorkSnapshot obj) => JsonSerializer.Serialize(obj, AgentWorkSnapshot.JsonOptions).GetHashCode();
    }
}
