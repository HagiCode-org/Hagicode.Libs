using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Codebuddy.Console;

public static class CodebuddyConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Codebuddy.Console",
        providerDisplayName: "CodeBuddy",
        defaultProviderName: "codebuddy",
        helpDescription: "Dedicated provider validation for the CodeBuddy CLI.",
        aliases: ["codebuddy-cli"],
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--model <model>       Override the CodeBuddy model for scenario runs"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Codebuddy.Console",
            "HagiCode.Libs.Codebuddy.Console --test-provider codebuddy-cli",
            "HagiCode.Libs.Codebuddy.Console --test-provider-full --repo ."
        ]);
}
