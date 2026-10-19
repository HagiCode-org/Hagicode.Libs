namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// Reads the image capability of a Copilot model before a run, so the caller can prepare images against that model's limits.
/// </summary>
public interface ICopilotModelCapabilityReader
{
    /// <summary>
    /// Returns the image capability of <paramref name="model"/>, or <see langword="null" /> when it is unknown: no model is
    /// given, the model is not in the list Copilot reports, its vision entry is missing, or the list cannot be read.
    /// The read starts a short-lived SDK client and disposes it before returning. Failures are not thrown to the caller,
    /// except when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="options">The Copilot options whose executable, environment and authentication the read uses.</param>
    /// <param name="model">The model id to look up.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    Task<CopilotModelImageCapability?> GetImageCapabilityAsync(
        CopilotOptions options,
        string? model,
        CancellationToken cancellationToken = default);
}
