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
        Register(name, provider, aliases: null);
    }

    /// <summary>
    /// Registers a provider instance under a name and additional aliases.
    /// </summary>
    /// <param name="name">The provider name.</param>
    /// <param name="provider">The provider instance.</param>
    /// <param name="aliases">Additional provider aliases.</param>
    public void Register(string name, ICliProvider provider, IEnumerable<string>? aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);

        RegisterCore(name, provider);

        if (aliases is null)
        {
            return;
        }

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            RegisterCore(alias, provider);
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
    /// Gets a typed provider by name.
    /// </summary>
    /// <typeparam name="TOptions">The provider option type.</typeparam>
    /// <param name="name">The provider name.</param>
    /// <returns>The typed provider instance, or <see langword="null" /> when not found.</returns>
    public ICliProvider<TOptions>? GetProvider<TOptions>(string name)
        where TOptions : class
    {
        return GetProvider(name) as ICliProvider<TOptions>;
    }

    /// <summary>
    /// Gets all registered providers.
    /// </summary>
    /// <returns>A read-only provider list.</returns>
    public IReadOnlyList<ICliProvider> GetAllProviders()
    {
        return _providers.Values.Distinct().ToArray();
    }

    private void RegisterCore(string name, ICliProvider provider)
    {
        if (!_providers.TryAdd(name, provider))
        {
            throw new InvalidOperationException($"Provider '{name}' has already been registered.");
        }
    }
}
