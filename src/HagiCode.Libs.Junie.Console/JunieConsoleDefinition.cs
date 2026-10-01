using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Junie.Console;

public static class JunieConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Junie.Console",
        providerDisplayName: "Junie",
        defaultProviderName: "junie",
        helpDescription: "Dedicated provider validation for the Junie CLI.",
        aliases: ["junie-cli"],
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--model <model>       Override the Junie model for scenario runs",
            "--executable <path>   Override the Junie executable path",
            "--arg <value>         Append one extra ACP bootstrap argument",
            "--auth-method <id>    Override the Junie authentication method",
            "--auth-token <token>  Copy a token into JUNIE_API_KEY"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Junie.Console",
            "HagiCode.Libs.Junie.Console --test-provider junie-cli",
            "HagiCode.Libs.Junie.Console --test-provider-full --model junie-k2.5 --arg --profile=smoke"
        ]);
}
