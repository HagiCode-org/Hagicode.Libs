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
            "--model <model>         Override the DeepAgents model",
            "--workspace <path>      Override the DeepAgents workspace root",
            "--name <name>           Override the DeepAgents agent name",
            "--description <text>    Override the DeepAgents agent description",
            "--skill <path>          Append one DeepAgents skill directory",
            "--memory <path>         Append one DeepAgents memory file",
            "--executable <path>     Override the DeepAgents executable path",
            "--arg <value>           Append one extra DeepAgents CLI argument"
        ],
        exampleLines:
        [
            "HagiCode.Libs.DeepAgents.Console",
            "HagiCode.Libs.DeepAgents.Console --test-provider deepagents-acp",
            "HagiCode.Libs.DeepAgents.Console --test-provider-full --model glm-5.1 --workspace . --skill ./skills --arg --debug"
        ]);
}
