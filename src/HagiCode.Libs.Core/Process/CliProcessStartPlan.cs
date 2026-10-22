namespace HagiCode.Libs.Core.Process;

/// <summary>
/// The start information for one CLI launch, with the path of the CLI that was resolved.
/// The start information may name <c>cmd.exe</c> for a command script, so the resolved path is kept beside it.
/// </summary>
/// <param name="StartInfo">The configured start information.</param>
/// <param name="ResolvedExecutablePath">The CLI path that the lookup resolved, such as a <c>.cmd</c> or <c>.exe</c> path.</param>
internal sealed record CliProcessStartPlan(System.Diagnostics.ProcessStartInfo StartInfo, string ResolvedExecutablePath);
