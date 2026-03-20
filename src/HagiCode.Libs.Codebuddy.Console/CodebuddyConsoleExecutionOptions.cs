using HagiCode.Libs.Providers.Codebuddy;

namespace HagiCode.Libs.Codebuddy.Console;

public sealed record CodebuddyConsoleExecutionOptions(
    string? Model,
    string? RepositoryPath)
{
    public const string DefaultModel = "glm-4.7";

    public static CodebuddyConsoleExecutionOptions Parse(IReadOnlyList<string> args)
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

        return new CodebuddyConsoleExecutionOptions(model, repositoryPath);
    }

    public CodebuddyOptions CreateBaseOptions()
    {
        return new CodebuddyOptions
        {
            Model = string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model,
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
