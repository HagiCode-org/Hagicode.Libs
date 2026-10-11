using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Dsh;

namespace HagiCode.Libs.Dsh.Console;

public sealed record DshConsoleExecutionOptions(
    string? Provider,
    string? Model,
    string? WorkspacePath,
    string? ExecutablePath,
    string? DshHomePath,
    IReadOnlyList<string> ExtraArguments,
    bool Verbose)
{
    public static DshConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? provider = null;
        string? model = null;
        string? workspacePath = null;
        string? executablePath = null;
        string? dshHomePath = null;
        var extraArguments = new List<string>();
        var verbose = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--provider":
                    provider = ReadValue(args, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(args, ref index, argument);
                    break;
                case "--workspace":
                    workspacePath = ReadValue(args, ref index, argument);
                    break;
                case "--executable":
                    executablePath = ReadValue(args, ref index, argument);
                    break;
                case "--dsh-home":
                    dshHomePath = ReadValue(args, ref index, argument);
                    break;
                case "--arg":
                    extraArguments.Add(ReadRawValue(args, ref index, argument));
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new DshConsoleExecutionOptions(
            provider,
            model,
            workspacePath,
            executablePath,
            dshHomePath,
            extraArguments,
            verbose);
    }

    public DshOptions CreateBaseOptions()
    {
        return new DshOptions
        {
            Provider = string.IsNullOrWhiteSpace(Provider) ? null : Provider,
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkspacePath) ? null : WorkspacePath,
            EnvironmentVariables = string.IsNullOrWhiteSpace(DshHomePath)
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["DSH_HOME"] = DshHomePath },
            ExtraArguments = ExtraArguments,
        };
    }

    public ProviderConsoleScenarioResult WithVerboseDetails(ProviderConsoleScenarioResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!Verbose)
        {
            return result;
        }

        return result with
        {
            DetailLines =
            [
                $"Provider: {(string.IsNullOrWhiteSpace(Provider) ? "not forwarded" : Provider)}",
                $"Model: {(string.IsNullOrWhiteSpace(Model) ? "not forwarded (dsh default)" : Model)}",
                $"Workspace: {(string.IsNullOrWhiteSpace(WorkspacePath) ? Directory.GetCurrentDirectory() : WorkspacePath)}"
            ]
        };
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        index++;
        return args[index];
    }

    private static string ReadRawValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        index++;
        return args[index];
    }
}
