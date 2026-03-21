using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Copilot.Console;

public static class CopilotConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Copilot.Console",
        providerDisplayName: "Copilot",
        defaultProviderName: "copilot",
        helpDescription: "Dedicated provider validation for GitHub Copilot.",
        aliases: ["github-copilot", "githubcopilot"],
        optionLines:
        [
            "--repo <path>              Include the repository analysis scenario in the suite",
            "--model <model>            Override the Copilot model for scenario runs",
            "--executable <path>        Override the Copilot executable path",
            "--auth-source <mode>       Select logged-in or token auth (logged-in|token)",
            "--github-token <token>     Override the GitHub token when auth-source=token",
            "--config-dir <path>        Forward a compatible Copilot config directory override",
            "--log-level <level>        Forward a compatible Copilot log level override"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Copilot.Console",
            "HagiCode.Libs.Copilot.Console --test-provider github-copilot",
            "HagiCode.Libs.Copilot.Console --test-provider-full --model claude-sonnet-4.5 --config-dir .copilot --repo ."
        ]);
}
