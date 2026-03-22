using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Pooling;

/// <summary>
/// Stores default provider-level pool settings.
/// </summary>
public sealed class CliProviderPoolConfigurationRegistry
{
    private readonly Dictionary<string, CliPoolSettings> _settings = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers default settings for a provider.
    /// </summary>
    public void Register(string providerName, CliPoolSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(settings);
        _settings[providerName] = settings;
    }

    /// <summary>
    /// Gets the configured settings for the provider.
    /// </summary>
    public CliPoolSettings GetSettings(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return _settings.TryGetValue(providerName, out var settings)
            ? settings
            : new CliPoolSettings();
    }
}
