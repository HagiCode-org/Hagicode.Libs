namespace HagiCode.Libs.Providers.Dsh;

/// <summary>
/// The three-state answer of a read-only image input probe.
/// </summary>
public enum DshImageInputState
{
    /// <summary>dsh advertised <c>promptCapabilities.image</c> as true on a fresh connection.</summary>
    Supported,

    /// <summary>dsh answered the handshake without advertising image input.</summary>
    NotAdvertised,

    /// <summary>The probe could not complete (executable missing, identity failed, timeout, or handshake error).</summary>
    Unknown
}

/// <summary>
/// The result of <see cref="DshProvider.ProbeImageInputAsync"/>. It reports the connection broadcast only. It does not
/// tell whether the selected model route can see pixels.
/// </summary>
/// <param name="State">The connection-level image input state.</param>
/// <param name="Reason">A short explanation for <see cref="DshImageInputState.Unknown"/>; null otherwise.</param>
public sealed record DshImageInputProbeResult(DshImageInputState State, string? Reason);
