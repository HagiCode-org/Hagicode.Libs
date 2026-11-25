using HagiCode.Libs.Providers.Antigravity;

namespace HagiCode.Libs.Antigravity.Console;

public sealed record AntigravityConsoleExecutionOptions(
    string? RepositoryPath,
    string? ExecutablePath,
    string? AuthenticationMethod,
    IReadOnlyList<string> ExtraArguments)
{
    public static AntigravityConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? repositoryPath = null;
        string? executablePath = null;
        string? authenticationMethod = null;
        var extraArguments = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
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
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new AntigravityConsoleExecutionOptions(
            repositoryPath,
            executablePath,
            authenticationMethod,
            extraArguments);
    }

    public AntigravityOptions CreateBaseOptions()
    {
        return new AntigravityOptions
        {
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            AuthenticationMethod = string.IsNullOrWhiteSpace(AuthenticationMethod) ? null : AuthenticationMethod,
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
