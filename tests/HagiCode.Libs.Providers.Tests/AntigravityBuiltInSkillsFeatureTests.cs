// Feature 2A.5 `built_in_skills`: are HagiCode-supplied skills loaded by agy, by what mechanism, and why do they fail.
//
// The loading mechanism is KNOWN (verified 2026-11-25 with agy 1.5.17): agy loads skills from <dir>/.agents/skills/<name>/SKILL.md
// for the working directory and for every top-level --add-dir <dir>. HagiCode stages the enabled built-in skills into that layout
// and passes the directory through AntigravityOptions.AdditionalDirectories. session/new still carries no skill payload, because
// the skills come from the command line, not from ACP. The scripted agent is SYNTHETIC; it is not agy.
// REAL test (Real_ prefix) needs HAGICODE_REAL_CLI_TESTS=1 and agy on PATH, skips with a reason otherwise, and is expected to
// on the 2026-11-25 run it passed and printed the skill names agy reports for itself, as a baseline for the mechanism work.
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravityBuiltInSkillsFeatureTests
{
    [Test]
    public async Task Session_new_carries_only_cwd_mcp_servers_and_settings_so_no_skill_payload_reaches_the_cli()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn());
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        var sessionNew = agent.RequestsFor("session/new").Single()["params"]!.AsObject();
        sessionNew.Select(static property => property.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["cwd", "mcpServers", "settings"]);
        sessionNew["mcpServers"]!.AsArray().Count.ShouldBe(0);
    }

    [Test]
    public void BuildCommandArguments_passes_each_added_directory_with_add_dir_before_acp()
    {
        var provider = new ScriptedAntigravityProvider(new AntigravityScriptedAgent());

        var arguments = provider.BuildCommandArguments(new AntigravityOptions
        {
            AdditionalDirectories = ["/vault/built-in-skills-agy/key", "   ", "/vault/other"]
        });

        // The skill layout is <dir>/.agents/skills, and --add-dir must come before the acp subcommand.
        arguments.ShouldBe(["--add-dir", "/vault/built-in-skills-agy/key", "--add-dir", "/vault/other", "acp", "--stdio"]);
    }

    [Test]
    public void Skill_directory_passed_as_a_raw_argument_reaches_the_command_line_unchanged()
    {
        // The only route in Libs today. Whether agy reads a flag like this is the open question for the real test.
        var provider = new AntigravityProvider(new StubAgyExecutableResolver(), new HagiCode.Libs.Core.Process.CliProcessManager(), null);

        var arguments = provider.BuildCommandArguments(new AntigravityOptions
        {
            ExtraArguments = ["--skills-dir=/opt/hagicode/skills"]
        });

        arguments.ShouldBe(["acp", "--stdio", "--skills-dir=/opt/hagicode/skills"]);
    }

    [Test]
    public void Provider_has_no_skill_input_of_its_own()
    {
        // Gap, recorded in the matrix: no option or provider member carries skills, so built-in skills cannot be requested.
        typeof(AntigravityOptions).GetProperties().Select(static property => property.Name)
            .Any(static name => name.Contains("Skill", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Plugin", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse();
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_agent_reports_the_skills_it_has_loaded_as_a_baseline()
    {
        AntigravityRealCli.RequireAgy();
        var provider = AntigravityRealCli.CreateProvider();

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { WorkingDirectory = Directory.GetCurrentDirectory() },
            "List the names of every skill you have loaded, one per line. If you have none, reply NONE."));

        messages[^1].Type.ShouldBe("terminal.completed");
        var answer = AntigravityTestHelpers.TextOf(messages);
        TestContext.Out.WriteLine($"agy skill baseline (agy 1.5.17):\n{answer}");
        answer.Trim().ShouldNotBeNullOrWhiteSpace();
    }
}
