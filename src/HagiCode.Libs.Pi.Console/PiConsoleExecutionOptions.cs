using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Pi.Console;

public sealed record PiConsoleExecutionOptions(
    string Provider,
    string Model,
    string? RepositoryPath,
    string? WorkspacePath,
    string? SessionDirectory,
    string? ExecutablePath,
    string? Thinking,
    IReadOnlyList<string> ExtraArguments)
{
    public const string DefaultProvider = "omniroute";
    public const string DefaultModel = "glm/glm-4.7";

    public static PiConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var provider = DefaultProvider;
        var model = DefaultModel;
        string? repositoryPath = null;
        string? workspacePath = null;
        string? sessionDirectory = null;
        string? executablePath = null;
        string? thinking = null;
        var extraArguments = new List<string>();

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
                case "--repo":
                    repositoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--workspace":
                    workspacePath = ReadValue(args, ref index, argument);
                    break;
                case "--session-dir":
                    sessionDirectory = ReadValue(args, ref index, argument);
                    break;
                case "--executable":
                    executablePath = ReadValue(args, ref index, argument);
                    break;
                case "--thinking":
                    thinking = ReadValue(args, ref index, argument);
                    break;
                case "--arg":
                    extraArguments.Add(ReadRawValue(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new PiConsoleExecutionOptions(
            provider,
            model,
            repositoryPath,
            workspacePath,
            sessionDirectory,
            executablePath,
            thinking,
            extraArguments);
    }

    public PiOptions CreateBaseOptions()
    {
        return new PiOptions
        {
            Provider = Provider,
            Model = Model,
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            Thinking = string.IsNullOrWhiteSpace(Thinking) ? null : Thinking,
            SessionDirectory = string.IsNullOrWhiteSpace(SessionDirectory) ? null : SessionDirectory,
            ExtraArguments = ExtraArguments,
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
