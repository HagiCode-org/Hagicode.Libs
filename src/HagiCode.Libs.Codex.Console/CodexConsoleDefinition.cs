using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Codex.Console;

public static class CodexConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Codex.Console",
        providerDisplayName: "Codex",
        defaultProviderName: "codex",
        helpDescription: "Dedicated provider validation for the Codex CLI.",
        aliases: ["codex-cli", "openai-codex"],
        optionLines:
        [
            "--repo <path>              Include the repository analysis scenario in the suite",
            "--model <model>            Override the Codex model for scenario runs",
            "--sandbox <mode>           Override the Codex sandbox mode",
            "--approval-policy <mode>   Override the Codex approval policy",
            "--api-key <key>            Override the Codex API key for scenario runs",
            "--base-url <url>           Override the Codex base URL for scenario runs"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Codex.Console",
            "HagiCode.Libs.Codex.Console --test-provider codex-cli",
            "HagiCode.Libs.Codex.Console --test-provider-full --sandbox workspace-write --repo ."
        ]);
}
