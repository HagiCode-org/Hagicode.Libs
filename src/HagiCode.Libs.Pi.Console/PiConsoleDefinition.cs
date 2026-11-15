using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Pi.Console;

public static class PiConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Pi.Console",
        providerDisplayName: "Pi",
        defaultProviderName: "pi",
        helpDescription: "Dedicated provider validation for the Pi CLI.",
        aliases: ["pi-cli"],
        optionLines:
        [
            "--provider <name>       Select the upstream Pi provider paired with --model (default: none, Pi native configuration)",
            "--model <model>         Override the Pi model (default: glm/glm-4.7)",
            "--repo <path>           Include the repository summary scenario in the suite",
            "--workspace <path>      Reuse a stable working directory for non-repo scenarios",
            "--session-dir <path>    Override the Pi session directory for resume scenarios",
            "--executable <path>     Override the Pi executable path",
            "--thinking <level>      Override the Pi thinking level",
            "--arg <value>           Append a raw extra Pi CLI argument"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Pi.Console",
            "HagiCode.Libs.Pi.Console --test-provider pi-cli",
            "HagiCode.Libs.Pi.Console --test-provider-full --provider <provider> --model <model> --repo ."
        ]);
}
