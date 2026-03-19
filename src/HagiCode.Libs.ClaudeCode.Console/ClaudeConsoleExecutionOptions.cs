using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console;

public sealed record ClaudeConsoleExecutionOptions(
    string? ApiKey,
    string? Model,
    string? RepositoryPath)
{
    public static ClaudeConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? apiKey = null;
        string? model = null;
        string? repositoryPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--api-key":
                    apiKey = ReadValue(args, ref index, argument);
                    break;
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

        return new ClaudeConsoleExecutionOptions(apiKey, model, repositoryPath);
    }

    public ClaudeCodeOptions CreateBaseOptions()
    {
        return new ClaudeCodeOptions
        {
            ApiKey = ApiKey,
            Model = Model,
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
