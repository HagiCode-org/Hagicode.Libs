using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Reasonix.Console;

public static class ReasonixConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Reasonix.Console",
        providerDisplayName: "Reasonix",
        defaultProviderName: "reasonix",
        helpDescription: "Dedicated provider validation for the Reasonix CLI.",
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--model <model>       Override the Reasonix ACP model selector",
            "--executable <path>   Override the Reasonix executable path",
            "--arg <value>         Append one extra ACP bootstrap argument"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Reasonix.Console",
            "HagiCode.Libs.Reasonix.Console --test-provider reasonix",
            "HagiCode.Libs.Reasonix.Console --test-provider-full --repo ."
        ]);
}
