using System.Reflection;
using GitHub.Copilot;

namespace HagiCode.Libs.Providers.Copilot;

internal enum CopilotFusionCompatibilityStatus
{
    Supported,
    Unsupported,
    Unknown
}

internal enum CopilotFusionOperation
{
    Create,
    Restore,
    Reset
}

internal sealed record CopilotFusionCompatibilityResult(
    CopilotFusionCompatibilityStatus Status,
    string Message);

/// <summary>
/// A Fusion compatibility refusal, either before session creation or while verifying the native session before send.
/// It is terminal: a retry or fresh session cannot make an unsupported configuration supported. It is still an
/// <see cref="InvalidOperationException"/>, so existing callers are unaffected.
/// </summary>
internal sealed class CopilotFusionCompatibilityException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

internal static class CopilotFusionCompatibility
{
    internal const bool GatewayMapsTypedFusionTier = true;

    internal static string? GetSdkVersion()
    {
        var assembly = typeof(CopilotClient).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informationalVersion?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString();
    }

    internal static bool HasTypedFusionTierApi =>
        typeof(CapiSessionOptions).GetProperty("FusionTier") is not null
        && typeof(SessionConfig).GetProperty("Capi")?.PropertyType == typeof(CapiSessionOptions)
        && typeof(ResumeSessionConfig).GetProperty("Capi")?.PropertyType == typeof(CapiSessionOptions);

    internal static bool IsFusionModel(string? model)
        => string.Equals(model, "fusion", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates Fusion intent before any client, environment lease or session operation.
    /// Omitted tiers use the runtime's native create/resume semantics; an explicit reset remains unsupported.
    /// </summary>
    internal static void ThrowIfGatewayCannotApply(CopilotSdkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hasInvalidTier = request.FusionTier is { } tier && !Enum.IsDefined(tier);
        var hasConcreteTierWithoutFusion = request.FusionTier is not null && !IsFusionModel(request.Model);
        if (hasInvalidTier || hasConcreteTierWithoutFusion)
        {
            var reason = hasInvalidTier
                ? "the Fusion tier is not one of standard, high, or max"
                : "a concrete Fusion tier was supplied with a non-Fusion model";
            throw new CopilotFusionCompatibilityException(
                $"[copilot_fusion_invalid] Fusion request model='{request.Model ?? "unspecified"}' is invalid: {reason}. " +
                $"Executable: {request.CliPath}; SDK version: {GetSdkVersion() ?? "unknown"}. " +
                "No session was created or restored, and no prompt was sent.");
        }

        if (!request.FusionTierIsExplicitlyUnset && (!IsFusionModel(request.Model) || HasTypedFusionTierApi))
        {
            return;
        }

        var requestedTier = request.FusionTier is { } requested
            ? requested.ToString().ToLowerInvariant()
            : "unspecified";
        var reasonText = request.FusionTierIsExplicitlyUnset
            ? "an explicit Fusion tier reset to the official default has no verified native reset on this SDK"
            : "the SDK gateway has no verified mapping from a Fusion tier into native create or restore session configuration";
        throw new CopilotFusionCompatibilityException(
            $"[copilot_fusion_unsupported] Fusion request model='{request.Model ?? "unspecified"}', requested tier='{requestedTier}' " +
            $"is unsupported by the SDK gateway. Executable: {request.CliPath}; SDK version: {GetSdkVersion() ?? "unknown"}. " +
            $"Reason: {reasonText}. No session was created or restored, and no prompt was sent.");
    }

    internal static void EnsureRuntimeApplied(
        string? requestedModel,
        CopilotFusionTier? requestedTier,
        string? actualModel,
        string? actualTier)
    {
        if (!IsFusionModel(requestedModel))
        {
            return;
        }

        // The native runtime reports Fusion under its canonical routing model id, "auto".
        if ((IsFusionModel(actualModel) || string.Equals(actualModel, "auto", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(actualTier)
            && (requestedTier is null || string.Equals(
                CopilotFusionTierJsonConverter.ToWireValue(requestedTier.Value), actualTier, StringComparison.Ordinal)))
        {
            return;
        }

        throw new CopilotFusionCompatibilityException(
            $"[copilot_fusion_unsupported] The runtime did not apply the requested Fusion configuration: " +
            $"requested tier='{requestedTier?.ToString().ToLowerInvariant() ?? "unspecified"}', " +
            $"actual model='{actualModel}', actual tier='{actualTier ?? "unknown"}'. No prompt was sent.");
    }

    internal static CopilotFusionCompatibilityResult Evaluate(
        string? executablePath,
        string? cliVersion,
        bool? cliSupportsTier,
        string? sdkVersion,
        bool sdkHasTypedTierApi,
        bool gatewayMapsTier,
        string? model,
        CopilotFusionTier? tier,
        CopilotFusionOperation operation,
        bool officialDefaultVerified,
        bool resetSupported)
    {
        var unsupportedReasons = new List<string>();
        var unknownReasons = new List<string>();

        if (tier is not null && !IsFusionModel(model))
        {
            unsupportedReasons.Add("a Fusion tier can only be used with model 'fusion'");
        }

        if (operation == CopilotFusionOperation.Reset && tier is not null)
        {
            unsupportedReasons.Add("an explicit Fusion tier reset cannot also specify a concrete tier");
        }

        if (cliVersion is null)
        {
            unknownReasons.Add("the resolved CLI version could not be determined");
        }

        if (cliSupportsTier is null)
        {
            unknownReasons.Add("the resolved CLI's tier capability could not be determined from its help or parser output");
        }
        else if (!cliSupportsTier.Value)
        {
            unsupportedReasons.Add("the resolved CLI does not support the '--fusion-tier' option");
        }

        if (sdkVersion is null)
        {
            unknownReasons.Add("the resolved GitHub.Copilot.SDK version could not be determined");
        }

        if (!sdkHasTypedTierApi)
        {
            unsupportedReasons.Add("the resolved GitHub.Copilot.SDK has no typed Fusion tier field for both create and restore configuration");
        }

        if (!gatewayMapsTier)
        {
            unsupportedReasons.Add("the SDK gateway does not map Fusion tiers into native session configuration");
        }

        if (tier is null && !officialDefaultVerified)
        {
            unknownReasons.Add("the official default for an unspecified Fusion tier is unverified");
        }

        if (operation == CopilotFusionOperation.Reset && !resetSupported)
        {
            unsupportedReasons.Add("native reset to the official Fusion default is unsupported or unverified");
        }

        var status = unsupportedReasons.Count > 0
            ? CopilotFusionCompatibilityStatus.Unsupported
            : unknownReasons.Count > 0
                ? CopilotFusionCompatibilityStatus.Unknown
                : CopilotFusionCompatibilityStatus.Supported;
        var reasons = unsupportedReasons.Concat(unknownReasons).ToArray();
        var versionDetails = $"CLI version: {cliVersion ?? "unknown"}; executable: {executablePath ?? "unknown"}; " +
                             $"SDK version: {sdkVersion ?? "unknown"}.";
        var requestedTier = tier?.ToString().ToLowerInvariant() ?? "unspecified";
        var category = status == CopilotFusionCompatibilityStatus.Unsupported
            ? "copilot_fusion_unsupported"
            : status == CopilotFusionCompatibilityStatus.Unknown
                ? "copilot_fusion_unknown"
                : "copilot_fusion_supported";
        var effectiveTier = status == CopilotFusionCompatibilityStatus.Supported
            ? tier?.ToString().ToLowerInvariant() ?? "official default"
            : "unknown";
        var message = $"[{category}] Fusion request model='{model ?? "unspecified"}', requested tier='{requestedTier}', " +
                      $"effective tier='{effectiveTier}' " +
                      $"is {status.ToString().ToLowerInvariant()}. {versionDetails} " +
                      (reasons.Length == 0 ? string.Empty : $"Reason: {string.Join("; ", reasons)}. ") +
                      (status == CopilotFusionCompatibilityStatus.Supported
                          ? string.Empty
                          : "No session was created or restored, and no prompt was sent.");

        return new CopilotFusionCompatibilityResult(status, message);
    }
}
