namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// The outcome of the Fusion compatibility preflight for one set of Copilot options.
/// </summary>
public enum CopilotFusionPreflightStatus
{
    /// <summary>The request is not a Fusion request, or the resolved executable and SDK support it.</summary>
    Supported,

    /// <summary>The compatibility gate refused the request as unsupported or unknown. Nothing was started or sent.</summary>
    Blocked,

    /// <summary>The check could not run, because no executable was resolved. The decision is left to the execution path.</summary>
    NotChecked
}

/// <summary>
/// The result of <see cref="ICopilotFusionCompatibilityPreflight.CheckFusionCompatibilityAsync"/>.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="Message">The gate's message for a blocked or not-checked outcome, naming the model, requested tier, versions and remediation; <see langword="null" /> when supported.</param>
public sealed record CopilotFusionPreflightResult(CopilotFusionPreflightStatus Status, string? Message);

/// <summary>
/// Runs the same Fusion compatibility decision that execution applies before a session operation, so a caller can reject a
/// Fusion request before it is queued. It starts no session and sends no prompt.
/// </summary>
public interface ICopilotFusionCompatibilityPreflight
{
    /// <summary>
    /// Evaluates the Fusion compatibility of <paramref name="options"/> against the resolved executable and SDK.
    /// </summary>
    /// <param name="options">The Copilot options whose model, tier and executable the decision uses.</param>
    /// <param name="cancellationToken">A token that cancels the probes.</param>
    Task<CopilotFusionPreflightResult> CheckFusionCompatibilityAsync(
        CopilotOptions options,
        CancellationToken cancellationToken = default);
}
