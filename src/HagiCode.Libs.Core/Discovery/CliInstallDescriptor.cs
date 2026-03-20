namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Describes a CLI provider's installation metadata, serving as the single source
/// of truth for npm package name, pinned version, executable candidates, and
/// public installability.
/// </summary>
/// <param name="ProviderName">The logical name of the provider (e.g., "ClaudeCode", "Codex").</param>
/// <param name="NpmPackage">The npm package name (e.g., "@anthropic-ai/claude-code").</param>
/// <param name="PinnedVersion">The pinned version string (e.g., "2.1.79").</param>
/// <param name="ExecutableCandidates">Ordered list of executable names to search on PATH.</param>
/// <param name="IsPubliclyInstallable">
/// Whether the CLI can be installed on public CI runners via npm without authentication.
/// </param>
public sealed record CliInstallDescriptor(
    string ProviderName,
    string NpmPackage,
    string PinnedVersion,
    string[] ExecutableCandidates,
    bool IsPubliclyInstallable)
{
    /// <summary>
    /// Gets the full npm package string with pinned version (e.g., "@anthropic-ai/claude-code@2.1.79").
    /// </summary>
    public string FullPackageSpecifier => $"{NpmPackage}@{PinnedVersion}";
}
