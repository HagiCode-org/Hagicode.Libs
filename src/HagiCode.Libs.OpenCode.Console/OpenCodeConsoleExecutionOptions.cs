using HagiCode.Libs.Providers.OpenCode;

namespace HagiCode.Libs.OpenCode.Console;

public sealed record OpenCodeConsoleExecutionOptions(
    string? Model,
    string? RepositoryPath,
    string? ExecutablePath,
    string? BaseUrl,
    string? Workspace,
    IReadOnlyList<string> ExtraArguments)
{
    public static OpenCodeConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? model = null;
        string? repositoryPath = null;
        string? executablePath = null;
        string? baseUrl = null;
        string? workspace = null;
        var extraArguments = new List<string>();
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
                case "--executable":
                    executablePath = ReadValue(args, ref index, argument);
                    break;
                case "--base-url":
                    baseUrl = ReadValue(args, ref index, argument);
                    break;
                case "--workspace":
                    workspace = ReadValue(args, ref index, argument);
                    break;
                case "--arg":
                    extraArguments.Add(ReadRawValue(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new OpenCodeConsoleExecutionOptions(
            model,
            repositoryPath,
            executablePath,
            baseUrl,
            workspace,
            extraArguments);
    }

    public OpenCodeOptions CreateBaseOptions()
    {
        return new OpenCodeOptions
        {
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl,
            Workspace = string.IsNullOrWhiteSpace(Workspace) ? null : Workspace,
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
