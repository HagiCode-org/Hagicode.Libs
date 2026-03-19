using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers;

/// <summary>
/// Defines a CLI provider that executes using typed options.
/// </summary>
/// <typeparam name="TOptions">The provider option type.</typeparam>
public interface ICliProvider<TOptions> : ICliProvider where TOptions : class
{
    /// <summary>
    /// Executes the provider with the supplied prompt and options.
    /// </summary>
    /// <param name="options">The provider options.</param>
    /// <param name="prompt">The user prompt.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    /// <returns>A stream of CLI messages.</returns>
    IAsyncEnumerable<CliMessage> ExecuteAsync(TOptions options, string prompt, CancellationToken cancellationToken = default);
}
