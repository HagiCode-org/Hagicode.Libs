using HagiCode.Libs.Providers.QoderCli;

namespace HagiCode.Libs.QoderCli.Console;

public sealed record QoderCliConsoleExecutionOptions(
    string? Model,
    string? RepositoryPath)
{
    public static QoderCliConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? model = null;
        string? repositoryPath = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--model":
                    model = ReadValue(args, ref index, argument);
                    break;
                case "--repo":
                    repositoryPath = ReadValue(args, ref index, argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new QoderCliConsoleExecutionOptions(model, repositoryPath);
    }

    public QoderCliOptions CreateBaseOptions()
    {
        return new QoderCliOptions
        {
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
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
}
