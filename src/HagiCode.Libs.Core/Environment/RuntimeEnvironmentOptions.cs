namespace HagiCode.Libs.Core.Environment;

/// <summary>
/// Configures runtime environment resolution behavior.
/// </summary>
public sealed class RuntimeEnvironmentOptions
{
    /// <summary>
    /// Gets or sets the cache duration for resolved environments.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the timeout for shell-based environment discovery.
    /// </summary>
    public TimeSpan ShellCommandTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
