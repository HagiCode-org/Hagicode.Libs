using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.OpenCode.Console;

public static class OpenCodeConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.OpenCode.Console",
        providerDisplayName: "OpenCode",
        defaultProviderName: "opencode",
        helpDescription: "Dedicated provider validation for the OpenCode CLI.",
        aliases: ["opencode-cli", "open-code"],
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--model <model>       Override the OpenCode model for scenario runs",
            "--executable <path>   Override the OpenCode executable path",
            "--base-url <url>      Attach to an existing OpenCode HTTP runtime",
            "--workspace <id>      Override the OpenCode workspace identifier",
            "--arg <value>         Append one extra `opencode serve` argument"
        ],
        exampleLines:
        [
            "HagiCode.Libs.OpenCode.Console",
            "HagiCode.Libs.OpenCode.Console --test-provider open-code",
            "HagiCode.Libs.OpenCode.Console --test-provider-full --model anthropic/claude-sonnet-4 --base-url http://127.0.0.1:4096",
            "HagiCode.Libs.OpenCode.Console --test-provider-full --repo /path/to/repo"
        ]);
}
