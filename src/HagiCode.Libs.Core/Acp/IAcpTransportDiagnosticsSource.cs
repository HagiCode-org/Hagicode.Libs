namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Exposes optional transport-level diagnostics such as subprocess stderr snapshots.
/// </summary>
public interface IAcpTransportDiagnosticsSource
{
    /// <summary>
    /// Returns the latest transport diagnostics snapshot, if any.
    /// </summary>
    /// <returns>The diagnostic snapshot, or <c>null</c> when no diagnostics are available.</returns>
    string? GetDiagnosticSummary();
}
