using System.Text.Json.Nodes;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class OpenCodeSkillPathsTests
{
    private const string Variable = "OPENCODE_CONFIG_CONTENT";

    [Test]
    public void Apply_without_skill_paths_returns_the_configured_environment_unchanged()
    {
        var environment = new Dictionary<string, string?> { ["A"] = "1" };

        OpenCodeSkillPaths.Apply(new OpenCodeOptions { EnvironmentVariables = environment }).ShouldBeSameAs(environment);
    }

    [Test]
    public void Apply_for_an_attached_runtime_leaves_the_environment_alone()
    {
        var environment = new Dictionary<string, string?>();

        OpenCodeSkillPaths.Apply(new OpenCodeOptions
        {
            BaseUrl = "http://127.0.0.1:4096",
            SkillPaths = ["/skills/a"],
            EnvironmentVariables = environment
        }).ShouldBeSameAs(environment);
    }

    [Test]
    public void Apply_builds_the_inline_configuration_with_absolute_distinct_paths()
    {
        var environment = OpenCodeSkillPaths.Apply(new OpenCodeOptions
        {
            SkillPaths = ["/skills/a", " ", "/skills/a", "/skills/b"],
            EnvironmentVariables = new Dictionary<string, string?> { [Variable] = null, ["KEEP"] = "yes" }
        });

        environment["KEEP"].ShouldBe("yes");
        ReadPaths(environment[Variable]).ShouldBe([Path.GetFullPath("/skills/a"), Path.GetFullPath("/skills/b")]);
    }

    [Test]
    public void Apply_extends_an_existing_configuration_and_keeps_its_other_keys_and_paths()
    {
        var existing = new JsonObject
        {
            ["model"] = "x/y",
            ["skills"] = new JsonObject { ["paths"] = new JsonArray("/mine", Path.GetFullPath("/skills/a")), ["urls"] = new JsonArray("https://e") }
        }.ToJsonString();

        var environment = OpenCodeSkillPaths.Apply(new OpenCodeOptions
        {
            SkillPaths = ["/skills/a", "/skills/b"],
            EnvironmentVariables = new Dictionary<string, string?> { [Variable] = existing }
        });

        var document = JsonNode.Parse(environment[Variable]!)!.AsObject();
        document["model"]!.GetValue<string>().ShouldBe("x/y");
        document["skills"]!["urls"]!.AsArray().Count.ShouldBe(1);
        ReadPaths(environment[Variable]).ShouldBe(["/mine", Path.GetFullPath("/skills/a"), Path.GetFullPath("/skills/b")]);
    }

    [TestCase("[]")]
    [TestCase("{\"skills\":[]}")]
    [TestCase("{\"skills\":{\"paths\":\"x\"}}")]
    [TestCase("not json")]
    public void Apply_rejects_an_existing_configuration_it_cannot_extend(string existing)
    {
        Should.Throw<InvalidOperationException>(() => OpenCodeSkillPaths.Apply(new OpenCodeOptions
        {
            SkillPaths = ["/skills/a"],
            EnvironmentVariables = new Dictionary<string, string?> { [Variable] = existing }
        }));
    }

    private static string[] ReadPaths(string? configuration)
    {
        return JsonNode.Parse(configuration!)!["skills"]!["paths"]!.AsArray().Select(static path => path!.GetValue<string>()).ToArray();
    }
}
