using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Kimi.Console;

public static class KimiConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Kimi.Console",
        providerDisplayName: "Kimi",
        defaultProviderName: "kimi",
        helpDescription: "Dedicated provider validation for the Kimi CLI.",
        aliases: ["kimi-cli"],
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--model <model>       Override the Kimi model for scenario runs",
            "--executable <path>   Override the Kimi executable path",
            "--arg <value>         Append one extra ACP bootstrap argument",
            "--auth-method <id>    Override the Kimi authentication method",
            "--auth-token <token>  Append a token to the Kimi auth payload"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Kimi.Console",
            "HagiCode.Libs.Kimi.Console --test-provider kimi-cli",
            "HagiCode.Libs.Kimi.Console --test-provider-full --model kimi-k2.5 --arg --profile=smoke"
        ]);
}
