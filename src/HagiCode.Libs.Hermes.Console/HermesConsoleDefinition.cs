using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Hermes.Console;

public static class HermesConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Hermes.Console",
        providerDisplayName: "Hermes",
        defaultProviderName: "hermes",
        helpDescription: "Dedicated provider validation for the Hermes CLI.",
        aliases: ["hermes-cli"],
        optionLines:
        [
            "--repo <path>              Include the repository summary scenario in the suite",
            "--model <model>            Override the Hermes model for scenario runs",
            "--executable <path>        Override the managed Hermes executable path",
            "--arguments <value>        Override the managed Hermes arguments (default: acp)"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Hermes.Console",
            "HagiCode.Libs.Hermes.Console --test-provider hermes-cli",
            "HagiCode.Libs.Hermes.Console --test-provider-full --repo .",
            "HagiCode.Libs.Hermes.Console --test-provider-full --arguments \"acp --profile smoke\""
        ]);
}
