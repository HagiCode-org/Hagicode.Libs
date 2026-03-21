using HagiCode.Libs.Providers.Copilot;

namespace HagiCode.Libs.Copilot.Console;

public sealed record CopilotConsoleExecutionOptions(
    string? ExecutablePath,
    string? GitHubToken,
    string? Model,
    string? RepositoryPath,
    string? ConfigDirectory,
    string? LogLevel,
    CopilotAuthSource AuthSource,
    IReadOnlyList<string> AdditionalArgs)
{
    public static CopilotConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? executablePath = null;
        string? gitHubToken = null;
        string? model = null;
        string? repositoryPath = null;
        string? configDirectory = null;
        string? logLevel = null;
        var authSource = CopilotAuthSource.LoggedInUser;
        var additionalArgs = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--executable":
                    executablePath = ReadValue(args, ref index, argument);
                    break;
                case "--github-token":
                    gitHubToken = ReadValue(args, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(args, ref index, argument);
                    break;
                case "--repo":
                    repositoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--config-dir":
                    configDirectory = ReadValue(args, ref index, argument);
                    additionalArgs.Add(argument);
                    additionalArgs.Add(configDirectory);
                    break;
                case "--log-level":
                    logLevel = ReadValue(args, ref index, argument);
                    additionalArgs.Add(argument);
                    additionalArgs.Add(logLevel);
                    break;
                case "--auth-source":
                    authSource = ParseAuthSource(ReadValue(args, ref index, argument));
                    break;
                default:
                    if (CopilotCliCompatibility.IsSupportedStandaloneFlag(argument))
                    {
                        additionalArgs.Add(argument);
                        break;
                    }

                    if (CopilotCliCompatibility.TryGetSupportedValueFlagArity(argument, out var valueCount))
                    {
                        additionalArgs.Add(argument);
                        for (var valueIndex = 0; valueIndex < valueCount; valueIndex++)
                        {
                            additionalArgs.Add(ReadValue(args, ref index, argument));
                        }

                        break;
                    }

                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        if (authSource == CopilotAuthSource.GitHubToken && string.IsNullOrWhiteSpace(gitHubToken))
        {
            throw new ArgumentException("--github-token is required when --auth-source token is selected.");
        }

        return new CopilotConsoleExecutionOptions(
            executablePath,
            gitHubToken,
            model,
            repositoryPath,
            configDirectory,
            logLevel,
            authSource,
            additionalArgs);
    }

    public CopilotOptions CreateBaseOptions()
    {
        return new CopilotOptions
        {
            ExecutablePath = ExecutablePath,
            GitHubToken = GitHubToken,
            Model = Model,
            AuthSource = AuthSource,
            AdditionalArgs = AdditionalArgs
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

    private static CopilotAuthSource ParseAuthSource(string value)
    {
        return value switch
        {
            "logged-in" => CopilotAuthSource.LoggedInUser,
            "token" => CopilotAuthSource.GitHubToken,
            _ => throw new ArgumentException($"Unsupported auth source: {value}")
        };
    }
}
