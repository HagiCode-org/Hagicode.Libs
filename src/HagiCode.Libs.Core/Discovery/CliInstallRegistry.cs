namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Centralized registry of all known CLI provider install descriptors.
/// This is the single source of truth for CLI metadata used by CI setup
/// and verification tooling.
/// </summary>
public static class CliInstallRegistry
{
    /// <summary>
    /// All registered CLI install descriptors.
    /// </summary>
    public static IReadOnlyList<CliInstallDescriptor> Descriptors { get; } =
    [
        new CliInstallDescriptor(
            ProviderName: "ClaudeCode",
            NpmPackage: "@anthropic-ai/claude-code",
            PinnedVersion: "2.1.79",
            ExecutableCandidates: ["claude", "claude-code"],
            IsPubliclyInstallable: true),

        new CliInstallDescriptor(
            ProviderName: "Copilot",
            NpmPackage: "@github/copilot",
            PinnedVersion: "1.0.10",
            ExecutableCandidates: ["copilot"],
            IsPubliclyInstallable: true),

        new CliInstallDescriptor(
            ProviderName: "Codex",
            NpmPackage: "@openai/codex",
            PinnedVersion: "0.115.0",
            ExecutableCandidates: ["codex", "codex-cli"],
            IsPubliclyInstallable: true),

        new CliInstallDescriptor(
            ProviderName: "OpenCode",
            NpmPackage: "opencode-ai",
            PinnedVersion: "1.3.3",
            ExecutableCandidates: ["opencode"],
            IsPubliclyInstallable: true),

        new CliInstallDescriptor(
            ProviderName: "Codebuddy",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["codebuddy", "codebuddy-cli"],
            IsPubliclyInstallable: false),

        new CliInstallDescriptor(
            ProviderName: "Gemini",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["gemini", "gemini-cli"],
            IsPubliclyInstallable: false),

        new CliInstallDescriptor(
            ProviderName: "Hermes",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["hermes", "hermes-cli"],
            IsPubliclyInstallable: false),
        new CliInstallDescriptor(
            ProviderName: "Kimi",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["kimi", "kimi-cli"],
            IsPubliclyInstallable: false),
        new CliInstallDescriptor(
            ProviderName: "Kiro",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["kiro", "kiro-cli"],
            IsPubliclyInstallable: false),
        new CliInstallDescriptor(
            ProviderName: "QoderCLI",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["qodercli"],
            IsPubliclyInstallable: false),
    ];

    /// <summary>
    /// Descriptors for CLIs that can be installed on public CI runners.
    /// </summary>
    public static IReadOnlyList<CliInstallDescriptor> PubliclyInstallable =>
        Descriptors.Where(d => d.IsPubliclyInstallable).ToArray();
}
