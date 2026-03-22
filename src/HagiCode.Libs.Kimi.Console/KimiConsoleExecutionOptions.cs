using HagiCode.Libs.Providers.Kimi;

namespace HagiCode.Libs.Kimi.Console;

public sealed record KimiConsoleExecutionOptions(
    string? Model,
    string? RepositoryPath,
    string? ExecutablePath,
    string? AuthenticationMethod,
    string? AuthenticationToken,
    IReadOnlyList<string> ExtraArguments)
{
    public static KimiConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? model = null;
        string? repositoryPath = null;
        string? executablePath = null;
        string? authenticationMethod = null;
        string? authenticationToken = null;
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
                case "--arg":
                    extraArguments.Add(ReadRawValue(args, ref index, argument));
                    break;
                case "--auth-method":
                    authenticationMethod = ReadValue(args, ref index, argument);
                    break;
                case "--auth-token":
                    authenticationToken = ReadValue(args, ref index, argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new KimiConsoleExecutionOptions(
            model,
            repositoryPath,
            executablePath,
            authenticationMethod,
            authenticationToken,
            extraArguments);
    }

    public KimiOptions CreateBaseOptions()
    {
        return new KimiOptions
        {
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            AuthenticationMethod = string.IsNullOrWhiteSpace(AuthenticationMethod) ? null : AuthenticationMethod,
            AuthenticationToken = string.IsNullOrWhiteSpace(AuthenticationToken) ? null : AuthenticationToken,
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
