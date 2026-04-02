namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

internal static class DeepAgentsToolcallCaseCatalog
{
    public const string Parsing = "parsing";
    public const string Failure = "failure";
    public const string Mixed = "mixed";

    private static readonly IReadOnlyDictionary<string, string> Cases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Normalize(Parsing)] = Parsing,
        [Normalize("success")] = Parsing,
        [Normalize(Failure)] = Failure,
        [Normalize("failed")] = Failure,
        [Normalize(Mixed)] = Mixed,
        [Normalize("mixed-transcript")] = Mixed,
        [Normalize("mixed_transcript")] = Mixed
    };

    public static IReadOnlyList<string> AvailableCases { get; } = [Parsing, Failure, Mixed];

    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Cases.TryGetValue(Normalize(value), out var resolved)
            ? resolved
            : null;
    }

    public static string FormatAvailableCases()
        => string.Join(", ", AvailableCases);

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return new string(
            value.Where(static character => char.IsLetterOrDigit(character))
                .Select(static character => char.ToLowerInvariant(character))
                .ToArray());
    }
}
