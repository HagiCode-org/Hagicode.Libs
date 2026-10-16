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
            PinnedVersion: "2.1.168",
            ExecutableCandidates: ["claude", "claude-code"],
            IsPubliclyInstallable: true)
        {
            InstallStrategies =
            [
                // npm global bin is supplied by PATH on Unix; on Windows the shim lives in %APPDATA%\npm.
                NpmStrategy(),

                // ~ is HOME on Unix and USERPROFILE on Windows, so one template covers both (~/.local/bin).
                // The installer accepts a version argument (bash -s <version>, or the PowerShell scriptblock argument).
                new CliInstallStrategy(CliInstallKind.NativeInstaller, CliPlatform.All, ["~/.local/bin"], SupportsVersionPin: true),

                // Cask claude-code has no documented version pin; /usr/local/bin is the Intel prefix and is unconfirmed.
                new CliInstallStrategy(CliInstallKind.Homebrew, CliPlatform.MacOS, ["/opt/homebrew/bin", "/usr/local/bin"], SupportsVersionPin: false),

                // winget install --version <X> is documented. The Links directory is unconfirmed (third-party sources only).
                new CliInstallStrategy(CliInstallKind.WinGet, CliPlatform.Windows, ["%LOCALAPPDATA%\\Microsoft\\WinGet\\Links"], SupportsVersionPin: true),

                new CliInstallStrategy(CliInstallKind.LegacyLocal, CliPlatform.All, ["~/.claude/local"], SupportsVersionPin: false),
            ],
        },

        new CliInstallDescriptor(
            ProviderName: "Copilot",
            NpmPackage: "@github/copilot",
            PinnedVersion: "1.0.60",
            ExecutableCandidates: ["copilot"],
            IsPubliclyInstallable: true)
        {
            VersionArgumentSets = CliVersionProbe.GetCopilotVersionArgumentSets(),
            InstallStrategies = [NpmStrategy()],
        },

        new CliInstallDescriptor(
            ProviderName: "Codex",
            NpmPackage: "@openai/codex",
            PinnedVersion: "0.115.0",
            ExecutableCandidates: ["codex", "codex-cli"],
            IsPubliclyInstallable: true)
        {
            InstallStrategies = [NpmStrategy()],
        },

        new CliInstallDescriptor(
            ProviderName: "DeepAgents",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["deepagents"],
            IsPubliclyInstallable: false),

        new CliInstallDescriptor(
            ProviderName: "OpenCode",
            NpmPackage: "opencode-ai",
            PinnedVersion: "1.3.3",
            ExecutableCandidates: ["opencode"],
            IsPubliclyInstallable: true),

        // The package requires Node >=22.19.0. It stays local-only and unpinned: the Desktop manifest owns the Pi version.
        new CliInstallDescriptor(
            ProviderName: "Pi",
            NpmPackage: "@earendil-works/pi-coding-agent",
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["pi", "pi-cli"],
            IsPubliclyInstallable: false),

        new CliInstallDescriptor(
            ProviderName: "Omp",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["omp", "omp-cli"],
            IsPubliclyInstallable: false),

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
            ProviderName: "Junie",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["junie", "junie-cli"],
            IsPubliclyInstallable: false),

        new CliInstallDescriptor(
            ProviderName: "Hermes",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["hermes", "hermes-cli"],
            IsPubliclyInstallable: false),
        new CliInstallDescriptor(
            ProviderName: "Dsh",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["dsh", "dsh-cli"],
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
            ExecutableCandidates: ["kiro-cli"],
            IsPubliclyInstallable: false),
        new CliInstallDescriptor(
            ProviderName: "QoderCLI",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["qodercli"],
            IsPubliclyInstallable: false),
        new CliInstallDescriptor(
            ProviderName: "Reasonix",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["reasonix"],
            IsPubliclyInstallable: false),
    ];

    /// <summary>
    /// The npm global install route shared by every npm-distributed CLI. Its Windows shim lives in <c>%APPDATA%\npm</c>.
    /// </summary>
    private static CliInstallStrategy NpmStrategy() =>
        new(CliInstallKind.Npm, CliPlatform.All, ["%APPDATA%\\npm"], SupportsVersionPin: true);

    /// <summary>
    /// Descriptors for CLIs that can be installed on public CI runners.
    /// </summary>
    public static IReadOnlyList<CliInstallDescriptor> PubliclyInstallable =>
        Descriptors.Where(d => d.IsPubliclyInstallable).ToArray();
}
