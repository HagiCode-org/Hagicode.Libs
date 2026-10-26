using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ClaudeInitSummaryTests
{
    [Test]
    public void TryParse_counts_prefixes_and_finds_no_unexposed_plugin_when_skills_are_present()
    {
        var skills = Enumerable.Range(0, 12).Select(index => $"hagi:skill-{index}")
            .Concat(Enumerable.Range(0, 24).Select(index => $"builtin-{index}"))
            .ToArray();
        var init = Init(
            version: "2.1.310",
            plugins: [new { name = "hagi", source = "hagi@inline", path = "/plugins/hagi" }],
            skills: skills,
            tools: ["Bash", "Artifact"]);

        ClaudeInitSummary.TryParse(init, out var summary).ShouldBeTrue();

        summary.Version.ShouldBe("2.1.310");
        summary.Skills.Count.ShouldBe(36);
        summary.SkillPrefixCounts["hagi"].ShouldBe(12);
        summary.SkillPrefixCounts["(none)"].ShouldBe(24);
        summary.HasArtifactTool.ShouldBeTrue();

        var findings = ClaudeInitSummary.FindUnexposedPlugins(summary, ["/plugins/hagi"]);
        findings.RegisteredWithoutSkills.ShouldBeEmpty();
        findings.PassedWithoutRegistration.ShouldBeEmpty();
    }

    [Test]
    public void FindUnexposedPlugins_reports_a_registered_plugin_whose_skills_are_missing_with_its_path()
    {
        var init = Init(
            plugins: [new { name = "hagi", source = "hagi@inline", path = "/plugins/hagi" }],
            skills: ["debug", "review"],
            tools: ["Bash"]);
        ClaudeInitSummary.TryParse(init, out var summary).ShouldBeTrue();

        var findings = ClaudeInitSummary.FindUnexposedPlugins(summary, ["/plugins/hagi"]);

        findings.RegisteredWithoutSkills.Count.ShouldBe(1);
        findings.RegisteredWithoutSkills[0].Name.ShouldBe("hagi");
        findings.RegisteredWithoutSkills[0].Path.ShouldBe("/plugins/hagi");
        findings.PassedWithoutRegistration.ShouldBeEmpty();
    }

    [Test]
    public void FindUnexposedPlugins_reports_a_passed_directory_that_init_did_not_register()
    {
        var init = Init(plugins: [], skills: ["debug"], tools: ["Bash"]);
        ClaudeInitSummary.TryParse(init, out var summary).ShouldBeTrue();

        var findings = ClaudeInitSummary.FindUnexposedPlugins(summary, ["/plugins/hagi"]);

        findings.PassedWithoutRegistration.ShouldBe(["/plugins/hagi"]);
        findings.RegisteredWithoutSkills.ShouldBeEmpty();
    }

    [Test]
    public void FindUnexposedPlugins_matches_a_directory_with_a_trailing_separator_to_the_registered_path()
    {
        var init = Init(
            plugins: [new { name = "hagi", source = "hagi@inline", path = "/plugins/hagi" }],
            skills: ["hagi:review"],
            tools: []);
        ClaudeInitSummary.TryParse(init, out var summary).ShouldBeTrue();

        var findings = ClaudeInitSummary.FindUnexposedPlugins(summary, ["/plugins/hagi/"]);

        findings.PassedWithoutRegistration.ShouldBeEmpty();
        findings.RegisteredWithoutSkills.ShouldBeEmpty();
    }

    [Test]
    public void FindUnexposedPlugins_reports_nothing_when_no_plugin_directory_was_passed()
    {
        var init = Init(plugins: [new { name = "hagi", source = "hagi@inline", path = "/plugins/hagi" }], skills: ["debug"], tools: []);
        ClaudeInitSummary.TryParse(init, out var summary).ShouldBeTrue();

        var findings = ClaudeInitSummary.FindUnexposedPlugins(summary, []);

        findings.RegisteredWithoutSkills.ShouldBeEmpty();
        findings.PassedWithoutRegistration.ShouldBeEmpty();
    }

    [Test]
    public void TryParse_treats_missing_or_mistyped_fields_as_empty_without_throwing()
    {
        var missing = JsonSerializer.SerializeToElement(new { type = "system", subtype = "init" });
        var mistyped = JsonSerializer.SerializeToElement(new { type = "system", subtype = "init", plugins = "hagi", skills = new { }, tools = 3 });

        ClaudeInitSummary.TryParse(missing, out var missingSummary).ShouldBeTrue();
        ClaudeInitSummary.TryParse(mistyped, out var mistypedSummary).ShouldBeTrue();

        foreach (var summary in new[] { missingSummary, mistypedSummary })
        {
            summary.Plugins.ShouldBeEmpty();
            summary.Skills.ShouldBeEmpty();
            summary.Tools.ShouldBeEmpty();
            summary.SkillPrefixCounts.ShouldBeEmpty();
            summary.HasArtifactTool.ShouldBeFalse();
        }
    }

    [Test]
    public void TryParse_returns_false_for_content_that_is_not_an_object()
    {
        var content = JsonSerializer.SerializeToElement("init");

        ClaudeInitSummary.TryParse(content, out var summary).ShouldBeFalse();

        summary.Skills.ShouldBeEmpty();
    }

    [Test]
    public void TryParse_detects_absent_artifact_tool()
    {
        var init = Init(plugins: [], skills: [], tools: ["Bash", "Read"]);

        ClaudeInitSummary.TryParse(init, out var summary).ShouldBeTrue();

        summary.HasArtifactTool.ShouldBeFalse();
        summary.Tools.Count.ShouldBe(2);
    }

    [Test]
    public void Describe_states_plugin_source_and_counts_but_not_skill_names()
    {
        var init = Init(
            version: "2.1.310",
            plugins: [new { name = "hagi", source = "hagi@inline", path = "/plugins/hagi" }],
            skills: ["hagi:secret-skill-name", "debug"],
            tools: ["Bash", "Artifact"]);
        ClaudeInitSummary.TryParse(init, out var summary).ShouldBeTrue();

        var description = summary.Describe();

        description.ShouldBe("version=2.1.310 plugins=hagi (hagi@inline) skills=2 skillPrefixes=(none)=1, hagi=1 tools=2 artifact=True");
        description.ShouldNotContain("secret-skill-name");
    }

    private static JsonElement Init(
        string? version = null,
        object[]? plugins = null,
        string[]? skills = null,
        string[]? tools = null)
    {
        return JsonSerializer.SerializeToElement(new
        {
            type = "system",
            subtype = "init",
            claude_code_version = version,
            plugins = plugins ?? [],
            skills = skills ?? [],
            tools = tools ?? []
        });
    }
}
