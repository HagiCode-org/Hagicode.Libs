using HagiCode.Libs.Providers.IFlow;

namespace HagiCode.Libs.IFlow.Console;

public sealed record IFlowConsoleExecutionOptions(
    string? Endpoint,
    string? ExecutablePath,
    string? Model,
    string? RepositoryPath)
{
    public static IFlowConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? endpoint = null;
        string? executablePath = null;
        string? model = null;
        string? repositoryPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--endpoint":
                    endpoint = ReadValue(args, ref index, argument);
                    ValidateEndpoint(endpoint);
                    break;
                case "--executable":
                    executablePath = ReadValue(args, ref index, argument);
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

        return new IFlowConsoleExecutionOptions(endpoint, executablePath, model, repositoryPath);
    }

    public IFlowOptions CreateBaseOptions()
    {
        return new IFlowOptions
        {
            Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? null : new Uri(Endpoint, UriKind.Absolute),
            ExecutablePath = ExecutablePath,
            Model = Model
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

    private static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Unsupported endpoint: {endpoint}");
        }
    }
}
