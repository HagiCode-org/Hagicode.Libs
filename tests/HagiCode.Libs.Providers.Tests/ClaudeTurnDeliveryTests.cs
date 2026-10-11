using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ClaudeTurnDeliveryTests
{
    [Fact]
    public void Accept_passes_messages_straight_through_when_no_prompt_is_awaited()
    {
        var delivery = new ClaudeTurnDelivery();

        Describe(delivery.Accept(Message("system", new { subtype = "init" }), false)).ShouldBe(["system"]);
        Describe(delivery.Accept(Message("result", new { result = "" }), false)).ShouldBe(["result"]);
    }

    [Fact]
    public void Accept_does_not_hide_a_turn_that_has_conversation_even_when_its_result_is_empty()
    {
        var delivery = new ClaudeTurnDelivery();

        delivery.Accept(Message("system", new { subtype = "init" }), true).ShouldBeEmpty();
        delivery.Accept(Message("assistant", new { content = "partial" }), true).ShouldBeEmpty();

        Describe(delivery.Accept(Message("result", new { result = "" }), true)).ShouldBe(["system", "assistant", "result"]);
    }

    [Fact]
    public void Accept_releases_an_injected_turn_that_ends_in_an_error_result()
    {
        var delivery = new ClaudeTurnDelivery();

        delivery.Accept(Message("system", new { subtype = "init" }), true).ShouldBeEmpty();

        Describe(delivery.Accept(Message("result", new { is_error = true, result = "" }), true))
            .ShouldBe(["system", "result"]);
    }

    [Fact]
    public void Accept_delivers_a_non_turn_message_while_a_prompt_is_awaited()
    {
        var delivery = new ClaudeTurnDelivery();

        Describe(delivery.Accept(Message("system", new { subtype = "task_notification", status = "stopped" }), true))
            .ShouldBe(["system"]);
    }

    [Fact]
    public void Accept_drops_the_injected_turn_and_releases_the_prompt_turn_once_it_is_replayed()
    {
        var delivery = new ClaudeTurnDelivery();

        delivery.Accept(Message("system", new { subtype = "init" }), true).ShouldBeEmpty();
        delivery.Accept(Message("result", new { result = "" }), true).ShouldBeEmpty();
        delivery.Accept(Message("system", new { subtype = "init" }), true).ShouldBeEmpty();

        Describe(delivery.Accept(Message("user", new { isReplay = true }), false)).ShouldBe(["system"]);
        Describe(delivery.Accept(Message("assistant", new { content = "answer" }), false)).ShouldBe(["assistant"]);
        Describe(delivery.Accept(Message("result", new { result = "answer" }), false)).ShouldBe(["result"]);
    }

    [Fact]
    public void Drain_returns_a_turn_that_was_still_open_when_the_run_ended()
    {
        var delivery = new ClaudeTurnDelivery();

        delivery.Accept(Message("system", new { subtype = "init" }), true).ShouldBeEmpty();
        delivery.Accept(Message("assistant", new { content = "unfinished" }), true).ShouldBeEmpty();

        Describe(delivery.Drain()).ShouldBe(["system", "assistant"]);
        delivery.Drain().ShouldBeEmpty();
    }

    private static CliMessage Message(string type, object payload)
    {
        return new CliMessage(type, JsonSerializer.SerializeToElement(payload));
    }

    private static string[] Describe(IEnumerable<CliMessage> messages)
    {
        return messages.Select(message => message.Type).ToArray();
    }
}
