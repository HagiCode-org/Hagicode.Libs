using HagiCode.Libs.Providers.Kiro;

namespace HagiCode.Libs.Kiro.Console;

public sealed record KiroConsoleExecutionOptions(
    string? Model,
    string? RepositoryPath,
    string? ExecutablePath,
    string? AuthenticationMethod,
    string? AuthenticationToken,
    string? BootstrapMethod,
    IReadOnlyList<string> ExtraArguments)
{
    public static KiroConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? model = null;
        string? repositoryPath = null;
        string? executablePath = null;
        string? authenticationMethod = null;
        string? authenticationToken = null;
        string? bootstrapMethod = null;
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
                case "--auth-method":
                    authenticationMethod = ReadValue(args, ref index, argument);
                    break;
                case "--auth-token":
                    authenticationToken = ReadValue(args, ref index, argument);
                    break;
                case "--bootstrap-method":
                    bootstrapMethod = ReadValue(args, ref index, argument);
                    break;
                case "--arg":
                    extraArguments.Add(ReadRawValue(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new KiroConsoleExecutionOptions(
            model,
            repositoryPath,
            executablePath,
            authenticationMethod,
            authenticationToken,
            bootstrapMethod,
            extraArguments);
    }

    public KiroOptions CreateBaseOptions()
    {
        return new KiroOptions
        {
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath,
            AuthenticationMethod = string.IsNullOrWhiteSpace(AuthenticationMethod) ? null : AuthenticationMethod,
            AuthenticationToken = string.IsNullOrWhiteSpace(AuthenticationToken) ? null : AuthenticationToken,
            BootstrapMethod = string.IsNullOrWhiteSpace(BootstrapMethod) ? null : BootstrapMethod,
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
