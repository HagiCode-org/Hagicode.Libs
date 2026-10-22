namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Resolves the executable a launch uses as one lookup result. The launch builds its not-found message from that same result,
/// so a failed launch walks the search path once instead of once for the launch and again for the message.
/// </summary>
internal static class CliLaunchLookup
{
    /// <summary>
    /// Returns the explicit path when it resolves. Without an explicit path, returns the provider lookup. When an explicit path is
    /// set but does not resolve, the provider lookup only explains the failure, so the result never names another executable.
    /// </summary>
    /// <param name="resolver">The resolver that performs the lookups.</param>
    /// <param name="providerName">The provider name that keys the install descriptor.</param>
    /// <param name="candidates">The provider's executable candidates.</param>
    /// <param name="explicitPath">The configured executable path, or <see langword="null" />.</param>
    /// <param name="environmentVariables">The environment that the lookups search.</param>
    /// <returns>The lookup result the launch uses and reports from.</returns>
    public static CliExecutableLookupResult Resolve(
        CliExecutableResolver resolver,
        string providerName,
        IEnumerable<string> candidates,
        string? explicitPath,
        IReadOnlyDictionary<string, string?> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (string.IsNullOrWhiteSpace(explicitPath))
        {
            return resolver.ResolveProviderExecutable(providerName, candidates, environmentVariables);
        }

        var resolved = resolver.ResolveExecutablePath(explicitPath, environmentVariables);
        if (resolved is not null)
        {
            return new CliExecutableLookupResult(resolved, null, []) { AcceptedPaths = [resolved] };
        }

        var providerLookup = resolver.ResolveProviderExecutable(providerName, candidates, environmentVariables);
        return providerLookup.IsFound ? new CliExecutableLookupResult(null, null, []) : providerLookup;
    }
}
