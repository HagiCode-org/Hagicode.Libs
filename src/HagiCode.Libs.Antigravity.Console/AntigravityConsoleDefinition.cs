using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Antigravity.Console;

public static class AntigravityConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Antigravity.Console",
        providerDisplayName: "Antigravity",
        defaultProviderName: "antigravity",
        helpDescription: "Dedicated provider validation for the Antigravity CLI (agy acp --stdio).",
        aliases: ["antigravity-cli"],
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--executable <path>   Override the Antigravity executable path",
            "--arg <value>         Append one extra argument after 'acp --stdio'",
            "--auth-method <id>    Invoke authenticate with this advertised method before the session starts"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Antigravity.Console",
            "HagiCode.Libs.Antigravity.Console --test-provider antigravity-cli",
            "HagiCode.Libs.Antigravity.Console --test-provider-full --repo /path/to/repo"
        ]);
}
