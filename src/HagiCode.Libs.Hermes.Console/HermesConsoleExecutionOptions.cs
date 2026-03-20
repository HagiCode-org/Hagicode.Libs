using System.Text;
using HagiCode.Libs.Providers.Hermes;

namespace HagiCode.Libs.Hermes.Console;

public sealed record HermesConsoleExecutionOptions(
    string? ExecutablePath,
    string? Model,
    string? RepositoryPath,
    IReadOnlyList<string> Arguments)
{
    public static HermesConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? executablePath = null;
        string? model = null;
        string? repositoryPath = null;
        IReadOnlyList<string> arguments = [];

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--executable":
                    executablePath = ReadValue(args, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(args, ref index, argument);
                    break;
                case "--repo":
                    repositoryPath = ReadValue(args, ref index, argument);
                    break;
                case "--arguments":
                    arguments = ParseArguments(ReadValue(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new HermesConsoleExecutionOptions(executablePath, model, repositoryPath, arguments);
    }

    public HermesOptions CreateBaseOptions()
    {
        return new HermesOptions
        {
            ExecutablePath = ExecutablePath,
            Model = Model,
            Arguments = Arguments
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

    private static IReadOnlyList<string> ParseArguments(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new ArgumentException("--arguments requires a non-empty value.");
        }

        var results = new List<string>();
        var current = new StringBuilder();
        char? activeQuote = null;

        foreach (var character in rawValue)
        {
            if (activeQuote.HasValue)
            {
                if (character == activeQuote.Value)
                {
                    activeQuote = null;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                activeQuote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                FlushToken(results, current);
                continue;
            }

            current.Append(character);
        }

        if (activeQuote.HasValue)
        {
            throw new ArgumentException("--arguments contains an unterminated quoted value.");
        }

        FlushToken(results, current);
        if (results.Count == 0)
        {
            throw new ArgumentException("--arguments requires at least one CLI argument.");
        }

        return results;
    }

    private static void FlushToken(List<string> results, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        results.Add(current.ToString());
        current.Clear();
    }
}
