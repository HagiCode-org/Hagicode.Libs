using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.IFlow.Console;

public static class IFlowConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.IFlow.Console",
        providerDisplayName: "IFlow",
        defaultProviderName: "iflow",
        helpDescription: "Dedicated provider validation for the IFlow CLI.",
        aliases: ["iflow-cli"],
        optionLines:
        [
            "--repo <path>              Include the repository summary scenario in the suite",
            "--model <model>            Override the IFlow model for scenario runs",
            "--endpoint <ws-url>        Connect to an existing ACP endpoint instead of starting iflow",
            "--executable <path>        Override the managed iflow executable path"
        ],
        exampleLines:
        [
            "HagiCode.Libs.IFlow.Console",
            "HagiCode.Libs.IFlow.Console --test-provider iflow-cli",
            "HagiCode.Libs.IFlow.Console --test-provider-full --repo .",
            "HagiCode.Libs.IFlow.Console --test-provider-full --endpoint ws://127.0.0.1:7331/acp"
        ]);
}
