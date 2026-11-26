// Feature 2A.7 `magpie` and `task_agent_family`: what each needs from a CLI, and whether Antigravity meets it.
//
// What the requirements are (hagicode-core, MagpieDtos.cs and AgentCliFeatureCatalog.cs):
//   magpie: an executor counts as connected only when `magpie ls` lists a model for its agent AND that agent's own
//           configuration points at the Magpie gateway address. The CLI must therefore list models and accept a gateway
//           address through its configuration.
//   task_agent_family: the CLI's provider name must be a family key that preset tasks can require. The family list lives in
//           hagicode-core (PresetTaskRequirementModels.cs), and "antigravity" is not in it yet (task 3.5). That part is not
//           testable from Libs and is recorded as a gap in the matrix.
// HERMETIC tests (SYNTHETIC scripted agent): the Libs side of both. The provider name, the DI registration, and whether the
// environment reaches the agy process. The gateway variable name below is a stand-in. The real one for agy is unknown.
// REAL test (Real_ prefix) needs HAGICODE_REAL_CLI_TESTS=1 and agy on PATH. It runs `agy models`, the CLI side of
// `magpie ls`. It is expected to pass now, because the probe listed models before the license reply appeared.
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Antigravity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravityMagpieFeatureTests
{
    private const string StandInGatewayVariable = "HAGICODE_TEST_GATEWAY_URL";
    private const string StandInGatewayAddress = "http://127.0.0.1:9/";

    [Test]
    public async Task Environment_variables_from_the_options_reach_the_agy_process_start()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("ok")));
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions
            {
                EnvironmentVariables = new Dictionary<string, string?> { [StandInGatewayVariable] = StandInGatewayAddress }
            },
            "hello"));

        provider.LastStartContext!.EnvironmentVariables!.ShouldContainKeyAndValue(StandInGatewayVariable, StandInGatewayAddress);
    }

    [Test]
    public void Provider_name_is_the_antigravity_family_key()
    {
        var provider = new AntigravityProvider(new StubAgyExecutableResolver(), new CliProcessManager(), null);

        provider.Name.ShouldBe("antigravity");
    }

    [Test]
    public async Task Registry_resolves_the_antigravity_provider_by_its_family_key()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = serviceProvider.GetRequiredService<ProviderRegistry>().GetProvider("antigravity");

        provider.ShouldNotBeNull();
        provider!.Name.ShouldBe("antigravity");
        provider.ShouldBeOfType<AntigravityProvider>();
    }

    [Test]
    public void Settings_and_model_flags_come_before_the_acp_subcommand()
    {
        var provider = new AntigravityProvider(new StubAgyExecutableResolver(), new CliProcessManager(), null);

        var arguments = provider.BuildCommandArguments(new AntigravityOptions
        {
            SettingsPath = "/home/user/.gemini/antigravity-cli/magpie-settings.json",
            Model = "magpie/claude/claude-haiku-5-5"
        });

        // `agy acp --settings` is rejected by the CLI, so the top-level flags must lead the list.
        arguments.ShouldBe(
        [
            "--settings", "/home/user/.gemini/antigravity-cli/magpie-settings.json",
            "--model", "magpie/claude/claude-haiku-5-5",
            "acp", "--stdio"
        ]);
    }

    [Test]
    public void Blank_settings_and_model_add_no_flags()
    {
        var provider = new AntigravityProvider(new StubAgyExecutableResolver(), new CliProcessManager(), null);

        var arguments = provider.BuildCommandArguments(new AntigravityOptions { SettingsPath = "  ", Model = null });

        arguments.ShouldBe(["acp", "--stdio"]);
    }

    [Test]
    public async Task Settings_and_model_reach_the_agy_process_start()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("ok")));
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { SettingsPath = "/tmp/magpie-settings.json", Model = "gpt-oss-120b-medium" },
            "hello"));

        provider.LastStartContext!.Arguments.ShouldBe(
            ["--settings", "/tmp/magpie-settings.json", "--model", "gpt-oss-120b-medium", "acp", "--stdio"]);
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_agy_lists_at_least_one_model_for_magpie_to_read()
    {
        var agyPath = AntigravityRealCli.RequireAgy();
        var workingDirectory = Directory.GetCurrentDirectory();

        var result = await new CliProcessManager().ExecuteAsync(new ProcessStartContext
        {
            ExecutablePath = agyPath,
            Arguments = ["models"],
            WorkingDirectory = workingDirectory,
            Timeout = TimeSpan.FromSeconds(120)
        });

        result.ExitCode.ShouldBe(0, result.StandardError);
        result.StandardOutput.Trim().ShouldNotBeNullOrWhiteSpace();
        TestContext.Out.WriteLine($"agy models:\n{result.StandardOutput}");
    }
}
