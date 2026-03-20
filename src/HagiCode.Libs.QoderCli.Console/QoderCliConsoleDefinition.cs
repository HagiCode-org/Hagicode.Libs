using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.QoderCli.Console;

public static class QoderCliConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.QoderCli.Console",
        providerDisplayName: "QoderCLI",
        defaultProviderName: "qodercli",
        helpDescription: "Dedicated provider validation for the QoderCLI CLI.",
        optionLines:
        [
            "--repo <path>         Include the repository summary scenario in the suite",
            "--model <model>       Override the QoderCLI model for scenario runs"
        ],
        exampleLines:
        [
            "HagiCode.Libs.QoderCli.Console",
            "HagiCode.Libs.QoderCli.Console --test-provider qodercli",
            "HagiCode.Libs.QoderCli.Console --test-provider-full --repo ."
        ]);
}
