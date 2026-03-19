namespace HagiCode.Libs.Providers;

/// <summary>
/// Stores registered CLI provider instances.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ICliProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a provider instance under a name.
    /// </summary>
    /// <param name="name">The provider name.</param>
    /// <param name="provider">The provider instance.</param>
    public void Register(string name, ICliProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);

        if (!_providers.TryAdd(name, provider))
        {
            throw new InvalidOperationException($"Provider '{name}' has already been registered.");
        }
    }

    /// <summary>
    /// Gets a provider by name.
    /// </summary>
    /// <param name="name">The provider name.</param>
    /// <returns>The provider instance, or <see langword="null" /> when not found.</returns>
    public ICliProvider? GetProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _providers.TryGetValue(name, out var provider) ? provider : null;
    }

    /// <summary>
    /// Gets all registered providers.
    /// </summary>
    /// <returns>A read-only provider list.</returns>
    public IReadOnlyList<ICliProvider> GetAllProviders()
    {
        return _providers.Values.ToArray();
    }
}
