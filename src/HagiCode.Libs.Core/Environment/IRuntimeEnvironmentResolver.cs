namespace HagiCode.Libs.Core.Environment;

/// <summary>
/// Resolves the runtime environment that should be applied to spawned CLI processes.
/// </summary>
public interface IRuntimeEnvironmentResolver
{
    /// <summary>
    /// Resolves the effective environment variables.
    /// </summary>
    /// <param name="cancellationToken">Cancels the resolution operation.</param>
    /// <returns>A read-only environment dictionary.</returns>
    Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default);
}
