using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Gemini.Console;

public static class GeminiConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Gemini.Console",
        providerDisplayName: "Gemini",
        defaultProviderName: "gemini",
        helpDescription: "Dedicated provider validation for the Gemini CLI.",
        aliases: ["gemini-cli"],
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--model <model>       Override the Gemini model for scenario runs",
            "--executable <path>   Override the Gemini executable path",
            "--arg <value>         Append one extra ACP bootstrap argument",
            "--auth-method <id>    Override the Gemini authentication method",
            "--auth-token <token>  Append a token to the Gemini auth payload"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Gemini.Console",
            "HagiCode.Libs.Gemini.Console --test-provider gemini-cli",
            "HagiCode.Libs.Gemini.Console --test-provider-full --model gemini-k2.5 --arg --profile=smoke"
        ]);
}
