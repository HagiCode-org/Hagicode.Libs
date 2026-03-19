namespace HagiCode.Libs.ConsoleTesting;

public sealed class ProviderConsoleDefinition
{
    private readonly HashSet<string> _normalizedAliases;

    public ProviderConsoleDefinition(
        string consoleName,
        string providerDisplayName,
        string defaultProviderName,
        string helpDescription,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? optionLines = null,
        IEnumerable<string>? exampleLines = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consoleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(helpDescription);

        ConsoleName = consoleName;
        ProviderDisplayName = providerDisplayName;
        DefaultProviderName = defaultProviderName;
        HelpDescription = helpDescription;

        var displayedAliases = new List<string>
        {
            defaultProviderName,
            providerDisplayName,
        };

        if (aliases != null)
        {
            displayedAliases.AddRange(aliases.Where(static alias => !string.IsNullOrWhiteSpace(alias)));
        }

        AllowedProviderAliases = displayedAliases
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _normalizedAliases = AllowedProviderAliases
            .Select(NormalizeAliasKey)
            .ToHashSet(StringComparer.Ordinal);

        OptionLines = optionLines?
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray() ?? [];
        ExampleLines = exampleLines?
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray() ?? [];
    }

    public string ConsoleName { get; }

    public string ProviderDisplayName { get; }

    public string DefaultProviderName { get; }

    public string HelpDescription { get; }

    public IReadOnlyList<string> AllowedProviderAliases { get; }

    public IReadOnlyList<string> OptionLines { get; }

    public IReadOnlyList<string> ExampleLines { get; }

    public string? NormalizeProviderAlias(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return DefaultProviderName;
        }

        return _normalizedAliases.Contains(NormalizeAliasKey(providerName))
            ? DefaultProviderName
            : null;
    }

    public string BuildUnsupportedProviderMessage(string commandName, string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        return $"Error: {commandName} only supports {DefaultProviderName}. " +
               $"'{providerName}' should use its own dedicated provider console.";
    }

    private static string NormalizeAliasKey(string value)
    {
        var chars = value
            .Where(static character => char.IsLetterOrDigit(character))
            .Select(static character => char.ToLowerInvariant(character))
            .ToArray();

        return new string(chars);
    }
}
