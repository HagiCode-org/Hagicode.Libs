namespace HagiCode.Libs.ConsoleTesting;

public static class ProviderConsoleCommandDispatcher
{
    public static async Task<int> DispatchAsync(
        string[] args,
        ProviderConsoleDefinition definition,
        IProviderConsoleRunner runner,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(runner);

        output ??= Console.Out;

        if (args.Length == 0)
        {
            var report = await runner.RunDefaultProviderSuiteAsync([], cancellationToken);
            return report.IsSuccess ? 0 : 1;
        }

        var command = args[0];
        return command switch
        {
            "--test-provider" or "-t" => await PingProviderAsync(definition, runner, args, output, cancellationToken),
            "--test-provider-full" or "-f" => await RunFullSuiteAsync(definition, runner, args, command, output, cancellationToken),
            "--test-all" or "-a" => await RunFullSuiteAsync(definition, runner, args, command, output, cancellationToken),
            "--help" or "-h" => ShowHelp(definition, output),
            _ => UnknownCommand(command, output)
        };
    }

    private static async Task<int> PingProviderAsync(
        ProviderConsoleDefinition definition,
        IProviderConsoleRunner runner,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var resolution = ResolveProviderRequest(definition, args, "--test-provider");
        if (resolution.ErrorMessage is not null)
        {
            output.WriteLine(resolution.ErrorMessage);
            return 1;
        }

        var result = await runner.PingProviderAsync(resolution.ProviderName!, resolution.AdditionalArgs, cancellationToken);
        return result is { Success: true } ? 0 : 1;
    }

    private static async Task<int> RunFullSuiteAsync(
        ProviderConsoleDefinition definition,
        IProviderConsoleRunner runner,
        IReadOnlyList<string> args,
        string commandName,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var resolution = ResolveProviderRequest(definition, args, commandName);
        if (resolution.ErrorMessage is not null)
        {
            output.WriteLine(resolution.ErrorMessage);
            return 1;
        }

        var report = await runner.RunProviderFullSuiteAsync(resolution.ProviderName!, resolution.AdditionalArgs, cancellationToken);
        return report.IsSuccess ? 0 : 1;
    }

    private static ProviderResolution ResolveProviderRequest(
        ProviderConsoleDefinition definition,
        IReadOnlyList<string> args,
        string commandName)
    {
        string? providerInput = null;
        var additionalArgsStartIndex = 1;

        if (args.Count >= 2 && !IsOption(args[1]))
        {
            providerInput = args[1];
            additionalArgsStartIndex = 2;
        }

        var providerName = definition.NormalizeProviderAlias(providerInput);
        if (providerName is null)
        {
            return new ProviderResolution(
                null,
                [],
                definition.BuildUnsupportedProviderMessage(commandName, providerInput!));
        }

        var additionalArgs = args.Skip(additionalArgsStartIndex).ToArray();
        return new ProviderResolution(providerName, additionalArgs, null);
    }

    private static bool IsOption(string token) => token.StartsWith("-", StringComparison.Ordinal);

    private static int ShowHelp(ProviderConsoleDefinition definition, TextWriter output)
    {
        output.WriteLine($"{definition.ConsoleName} - {definition.HelpDescription}");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine($"  {definition.ConsoleName} [command] [provider] [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine($"  (none)                    Run the default {definition.ProviderDisplayName} suite");
        output.WriteLine($"  --test-provider, -t      Ping {definition.DefaultProviderName}");
        output.WriteLine($"  --test-provider-full, -f Run the full {definition.ProviderDisplayName} suite");
        output.WriteLine($"  --test-all, -a           Alias for the full {definition.ProviderDisplayName} suite");
        output.WriteLine("  --help, -h               Show this help message");

        if (definition.OptionLines.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Options:");
            foreach (var optionLine in definition.OptionLines)
            {
                output.WriteLine($"  {optionLine}");
            }
        }

        output.WriteLine();
        output.WriteLine($"Supported aliases: {string.Join(", ", definition.AllowedProviderAliases)}");
        output.WriteLine("Other providers should use their own dedicated provider consoles.");

        if (definition.ExampleLines.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Examples:");
            foreach (var exampleLine in definition.ExampleLines)
            {
                output.WriteLine($"  {exampleLine}");
            }
        }

        return 0;
    }

    private static int UnknownCommand(string command, TextWriter output)
    {
        output.WriteLine($"Unknown command: {command}");
        output.WriteLine("Use --help to see available commands.");
        return 1;
    }

    private sealed record ProviderResolution(
        string? ProviderName,
        IReadOnlyList<string> AdditionalArgs,
        string? ErrorMessage);
}
