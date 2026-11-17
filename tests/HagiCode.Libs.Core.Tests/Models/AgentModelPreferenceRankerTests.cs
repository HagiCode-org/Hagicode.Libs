using HagiCode.Libs.Core.Models;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Models;

public sealed class AgentModelPreferenceRankerTests
{
    private static AgentModelPreferenceCandidate M(string id, string group, int sortOrder = 0, string? name = null) =>
        new(id, name ?? id, sortOrder, group);

    private static string[] Ids(IEnumerable<AgentModelPreferenceCandidate> models) => models.Select(m => m.Id).ToArray();

    [Test]
    public void Each_declared_group_is_kept_as_is()
    {
        ModelPreferenceGroup.Normalize("claude").ShouldBe("claude");
        ModelPreferenceGroup.Normalize("claude-compatible").ShouldBe("claude-compatible");
        ModelPreferenceGroup.Normalize("gpt").ShouldBe("gpt");
        ModelPreferenceGroup.Normalize("other").ShouldBe("other");
    }

    [Test]
    public void Undeclared_or_unknown_group_is_treated_as_other()
    {
        ModelPreferenceGroup.Normalize(null).ShouldBe("other");
        ModelPreferenceGroup.Normalize("   ").ShouldBe("other");
        ModelPreferenceGroup.Normalize("anthropic").ShouldBe("other");

        var ranked = AgentModelPreferenceRanker.Order("claude", [M("undeclared", null!, 1), M("claude-x", "claude", 2)]);
        Ids(ranked).ShouldBe(["claude-x", "undeclared"]);
    }

    [Test]
    public void Codex_puts_gpt_first_and_keeps_catalog_order_for_the_rest()
    {
        var ranked = AgentModelPreferenceRanker.Order("codex",
        [
            M("claude-opus", "claude", 1),
            M("glm", "claude-compatible", 2),
            M("gpt-b", "gpt", 4),
            M("gemini", "other", 3),
            M("gpt-a", "gpt", 5),
        ]);

        Ids(ranked).ShouldBe(["gpt-b", "gpt-a", "claude-opus", "glm", "gemini"]);
    }

    [Test]
    public void Claude_orders_claude_then_compatible_then_gpt_then_other()
    {
        var ranked = AgentModelPreferenceRanker.Order("claude",
        [
            M("gemini", "other", 1),
            M("gpt", "gpt", 2),
            M("glm", "claude-compatible", 3),
            M("opus", "claude", 4),
            M("minimax", "claude-compatible", 5),
        ]);

        Ids(ranked).ShouldBe(["opus", "glm", "minimax", "gpt", "gemini"]);
    }

    [Test]
    public void Family_without_profile_keeps_catalog_order()
    {
        AgentModelPreferenceRanker.HasProfile("kimi").ShouldBeFalse();

        var ranked = AgentModelPreferenceRanker.Order("kimi",
        [
            M("gpt", "gpt", 3),
            M("opus", "claude", 1),
            M("glm", "claude-compatible", 2),
        ]);

        Ids(ranked).ShouldBe(["opus", "glm", "gpt"]);
    }

    [Test]
    public void Family_lookup_is_case_and_space_insensitive()
    {
        AgentModelPreferenceRanker.HasProfile(" Codex ").ShouldBeTrue();
        Ids(AgentModelPreferenceRanker.Order("CODEX", [M("claude", "claude", 1), M("gpt", "gpt", 2)])).ShouldBe(["gpt", "claude"]);
    }

    [Test]
    public void Equal_group_ties_are_broken_by_sort_order_then_name_then_id()
    {
        var ranked = AgentModelPreferenceRanker.Order("claude",
        [
            M("id-c", "claude", 2, "Same"),
            M("id-b", "claude", 1, "Zeta"),
            M("id-a", "claude", 2, "Same"),
            M("id-d", "claude", 2, "Alpha"),
        ]);

        Ids(ranked).ShouldBe(["id-b", "id-d", "id-a", "id-c"]);
    }

    [Test]
    public void Ranking_is_repeatable_for_the_same_input()
    {
        var input = new[] { M("b", "gpt", 1), M("a", "gpt", 1), M("c", "claude", 0) };
        Ids(AgentModelPreferenceRanker.Order("codex", input)).ShouldBe(Ids(AgentModelPreferenceRanker.Order("codex", input.Reverse())));
    }
}
