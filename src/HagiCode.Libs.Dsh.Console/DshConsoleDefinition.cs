using HagiCode.Libs.ConsoleTesting;

namespace HagiCode.Libs.Dsh.Console;

public static class DshConsoleDefinition
{
    public static ProviderConsoleDefinition Instance { get; } = new(
        consoleName: "HagiCode.Libs.Dsh.Console",
        providerDisplayName: "DeepSeek Harness",
        defaultProviderName: "dsh",
        helpDescription: "Dedicated provider validation for the DeepSeek Harness (dsh) ACP CLI.",
        aliases: ["dsh-cli"],
        optionLines:
        [
            "--executable <path>   Override the dsh executable path",
            "--provider <id>       Select the dsh provider paired with --model, e.g. omniroute (required with --model)",
            "--model <model>       Select one dsh model exactly as dsh advertises it, e.g. claude/claude-haiku-5-5 (no default is applied)",
            "--arg <value>         Append one extra ACP bootstrap argument",
            "--workspace <path>    Bind scenario sessions to this workspace directory",
            "--dsh-home <path>     Set DSH_HOME for the dsh process",
            "--verbose             Print the model and workspace for each scenario"
        ],
        exampleLines:
        [
            "HagiCode.Libs.Dsh.Console",
            "HagiCode.Libs.Dsh.Console --test-provider dsh-cli",
            "HagiCode.Libs.Dsh.Console --test-provider-full --provider omniroute --model claude/claude-haiku-5-5 --dsh-home <test-home> --verbose"
        ]);
}
