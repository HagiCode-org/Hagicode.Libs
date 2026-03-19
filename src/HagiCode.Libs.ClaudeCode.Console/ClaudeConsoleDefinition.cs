using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.ClaudeCode.Console;

public static class ClaudeConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.ClaudeCode.Console",
        providerDisplayName: "Claude Code",
        defaultProviderName: "claude-code",
        helpDescription: "Dedicated provider validation for the Claude Code CLI.",
        aliases: ["claude", "claudecode", "anthropic-claude"],
        optionLines:
        [
            "--repo <path>         Include the repository analysis scenario in the suite",
            "--api-key <key>       Override the Anthropic API key for scenario runs",
            "--model <model>       Override the Claude model for scenario runs"
        ],
        exampleLines:
        [
            "HagiCode.Libs.ClaudeCode.Console",
            "HagiCode.Libs.ClaudeCode.Console --test-provider claude",
            "HagiCode.Libs.ClaudeCode.Console --test-provider-full --repo ."
        ]);
}
