namespace HagiCode.Libs.Core.Environment;

/// <summary>
/// Merges the environment of one provider run from three sources, in this precedence (lowest first):
/// the values a caller injects for the run, the resolved runtime environment (the process environment and the server values),
/// and the user's configured environment. A user-set value therefore always wins over an injected one.
/// </summary>
public static class RunEnvironment
{
    /// <param name="injected">Run-scoped values from the caller. A null or blank value is dropped, never passed as an empty string.</param>
    /// <param name="resolved">The resolver's result. A null value removes the variable from the child process.</param>
    /// <param name="configured">The user's configured values. A null value removes the variable from the child process.</param>
    /// <returns>A new dictionary; no input is mutated. Keys are compared ordinally, as the providers already do.</returns>
    public static IReadOnlyDictionary<string, string?> Merge(
        IReadOnlyDictionary<string, string?>? injected,
        IReadOnlyDictionary<string, string?>? resolved,
        IReadOnlyDictionary<string, string?>? configured)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (injected is not null)
        {
            foreach (var (key, value) in injected)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    merged[key] = value;
                }
            }
        }

        if (resolved is not null)
        {
            foreach (var (key, value) in resolved)
            {
                merged[key] = value;
            }
        }

        if (configured is not null)
        {
            foreach (var (key, value) in configured)
            {
                merged[key] = value;
            }
        }

        return merged;
    }
}
