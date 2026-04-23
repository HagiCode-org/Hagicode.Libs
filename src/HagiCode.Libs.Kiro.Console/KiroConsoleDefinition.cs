using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Kiro.Console;

public static class KiroConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Kiro.Console",
        providerDisplayName: "Kiro",
        defaultProviderName: "kiro-cli",
        helpDescription: "Dedicated provider validation for the Kiro CLI.",
        aliases: [],
        optionLines:
        [
            "--repo <path>              Include the repository summary scenario in the suite",
            "--model <model>            Override the Kiro model for scenario runs",
            "--executable <path>        Override the Kiro executable path",
            "--auth-method <id>         Select the advertised authentication method",
            "--auth-token <token>       Forward a token into the authentication payload",
            "--bootstrap-method <name>  Override the bootstrap RPC method name",
            "--arg <value>              Append a raw extra CLI argument (repeatable)"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Kiro.Console",
            "HagiCode.Libs.Kiro.Console --test-provider kiro-cli",
            "HagiCode.Libs.Kiro.Console --test-provider-full --model kiro-default --repo .",
            "HagiCode.Libs.Kiro.Console --test-provider-full --auth-method token --auth-token <token> --arg --profile"
        ]);
}
