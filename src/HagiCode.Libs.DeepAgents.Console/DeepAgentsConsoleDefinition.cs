using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.DeepAgents.Console;

public static class DeepAgentsConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.DeepAgents.Console",
        providerDisplayName: "DeepAgents",
        defaultProviderName: "deepagents",
        helpDescription: "Dedicated provider validation for the DeepAgents ACP CLI.",
        aliases: ["deepagents-acp"],
        optionLines:
        [
            "--repo <path>           Include the repository summary scenario in the suite",
            "--workspace <path>      Override the DeepAgents workspace root for scenario runs",
            "--model <model>         Override the DeepAgents model",
            "--mode-id <id>          Override the DeepAgents ACP session mode (for example bypassPermissions)",
            "--name <name>           Override the DeepAgents agent name",
            "--description <text>    Override the DeepAgents agent description",
            "--verbose, -v           Print scenario-level execution details",
            "--toolcall              Append the DeepAgents toolcall diagnostics scenarios",
            "--toolcall-case <name>  Run only one toolcall diagnostics case: parsing, failure, mixed",
            "--skill <path>          Append one DeepAgents skill directory",
            "--memory <path>         Append one DeepAgents memory file",
            "--executable <path>     Override the DeepAgents executable path",
            "--arg <value>           Append one extra DeepAgents CLI argument"
        ],
        exampleLines:
        [
            "HagiCode.Libs.DeepAgents.Console",
            "HagiCode.Libs.DeepAgents.Console --test-provider deepagents-acp",
            "HagiCode.Libs.DeepAgents.Console --test-provider-full --model anthropic:glm-5.1 --workspace . --verbose",
            "HagiCode.Libs.DeepAgents.Console --test-provider-full --model glm-5.1 --skill ./skills --arg --debug",
            "HagiCode.Libs.DeepAgents.Console --test-provider-full --toolcall --verbose",
            "HagiCode.Libs.DeepAgents.Console --test-provider-full --toolcall-case mixed",
            "HagiCode.Libs.DeepAgents.Console --test-provider-full --mode-id bypassPermissions --verbose"
        ]);
}
