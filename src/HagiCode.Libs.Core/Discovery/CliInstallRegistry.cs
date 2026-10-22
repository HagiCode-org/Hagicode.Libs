using System.Runtime.InteropServices;

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
            // The platform package ships claude.exe at its root (checked at 2.1.168 and 2.1.307). The arm64 package name and
            // the top-level bin\claude.exe are unverified; the top-level file is tried last because it is an installer script.
            WindowsExecutablePolicy = CliWindowsExecutablePolicy.RequireNative,
            WindowsExecutableEvidence =
                "RequireNative. @anthropic-ai/claude-code-win32-x64 holds claude.exe at the package root (pin 2.1.168, current 2.1.307). "
                + "The -win32-arm64 name comes from the optional dependencies and its file list was not read. "
                + "The first npm publish of the platform package is 0.0.0 (2026-04-13); the oldest release that ships the .exe was not established.",
            PackagedBinaryLayout = new CliPackagedBinaryLayout(
                MainPackage: "@anthropic-ai/claude-code",
                Targets:
                [
                    new CliPackagedBinaryTarget(Architecture.X64, "@anthropic-ai/claude-code-win32-x64", "claude.exe"),
                    new CliPackagedBinaryTarget(Architecture.Arm64, "@anthropic-ai/claude-code-win32-arm64", "claude.exe"),
                ],
                MainPackageFile: "bin\\claude.exe"),
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
            // @github/copilot is a Node loader; the native copilot.exe is in @github/copilot-win32-x64 (checked at 1.0.60 and 1.0.102).
            WindowsExecutablePolicy = CliWindowsExecutablePolicy.RequireNative,
            WindowsExecutableEvidence =
                "RequireNative. @github/copilot-win32-x64 holds copilot.exe at the package root (pin 1.0.60, current 1.0.102); the main package is a Node loader. "
                + "The -win32-arm64 name comes from the optional dependencies and its file list was not read. "
                + "The first npm publish of the platform package is 1.0.0 (2025-12-16); the oldest release that ships the .exe was not established.",
            PackagedBinaryLayout = new CliPackagedBinaryLayout(
                MainPackage: "@github/copilot",
                Targets:
                [
                    new CliPackagedBinaryTarget(Architecture.X64, "@github/copilot-win32-x64", "copilot.exe"),
                    new CliPackagedBinaryTarget(Architecture.Arm64, "@github/copilot-win32-arm64", "copilot.exe"),
                ]),
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
            // @openai/codex is a Node script; the native codex.exe is under vendor\<triple>\codex in the win32 platform package (checked at 0.115.0 and 0.163.0).
            WindowsExecutablePolicy = CliWindowsExecutablePolicy.RequireNative,
            WindowsExecutableEvidence =
                "RequireNative. @openai/codex-win32-x64 (an alias of @openai/codex@<version>-win32-x64) holds vendor\\x86_64-pc-windows-msvc\\codex\\codex.exe "
                + "(pin 0.115.0, current 0.163.0); @openai/codex is a Node script. The arm64 triple and package name were not read. "
                + "The oldest release that ships the .exe was not established (the alias is not a standalone npm package name).",
            PackagedBinaryLayout = new CliPackagedBinaryLayout(
                MainPackage: "@openai/codex",
                Targets:
                [
                    new CliPackagedBinaryTarget(Architecture.X64, "@openai/codex-win32-x64", "vendor\\x86_64-pc-windows-msvc\\codex\\codex.exe"),
                    new CliPackagedBinaryTarget(Architecture.Arm64, "@openai/codex-win32-arm64", "vendor\\aarch64-pc-windows-msvc\\codex\\codex.exe"),
                ]),
            InstallStrategies = [NpmStrategy()],
        },

        new CliInstallDescriptor(
            ProviderName: "DeepAgents",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["deepagents"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, not examined. The launcher is a Python tool (deepagents or uvx), not an npm package.",
        },

        new CliInstallDescriptor(
            ProviderName: "OpenCode",
            NpmPackage: "opencode-ai",
            PinnedVersion: "1.3.3",
            ExecutableCandidates: ["opencode"],
            IsPubliclyInstallable: true)
        {
            // opencode-ai ships bin\opencode.exe at 1.19.3 and bin\opencode (no extension) at the pin 1.3.3. The platform package holds the real binary.
            WindowsExecutableEvidence =
                "PreferNative with a packaged layout. opencode-windows-x64 holds bin\\opencode.exe at the pin (1.3.3) and at 1.19.3; "
                + "-windows-x64-baseline and -windows-arm64 come from the optional dependencies and were not read. "
                + "The supported Windows install routes were not checked, so promotion to RequireNative waits for that.",
            PackagedBinaryLayout = new CliPackagedBinaryLayout(
                MainPackage: "opencode-ai",
                Targets:
                [
                    new CliPackagedBinaryTarget(Architecture.X64, "opencode-windows-x64", "bin\\opencode.exe"),
                    new CliPackagedBinaryTarget(Architecture.X64, "opencode-windows-x64-baseline", "bin\\opencode.exe"),
                    new CliPackagedBinaryTarget(Architecture.Arm64, "opencode-windows-arm64", "bin\\opencode.exe"),
                ],
                MainPackageFile: "bin\\opencode.exe"),
            InstallStrategies = [NpmStrategy()],
        },

        // The package requires Node >=22.19.0. It stays local-only and unpinned: the Desktop manifest owns the Pi version.
        new CliInstallDescriptor(
            ProviderName: "Pi",
            NpmPackage: "@earendil-works/pi-coding-agent",
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["pi", "pi-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, script only. The package is a Node script (dist/bundle/cli.js, checked at 1.4.0) and no native binary was found.",
        },

        new CliInstallDescriptor(
            ProviderName: "Omp",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["omp", "omp-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, script only. @oh-my-pi/pi-coding-agent is a Node script (dist/cli.js, checked at 18.12.6); no native binary was found.",
        },

        new CliInstallDescriptor(
            ProviderName: "Codebuddy",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["codebuddy", "codebuddy-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, script only. @tencent-ai/codebuddy-code (checked at 2.172.1) is a ./bin/codebuddy script; the file list was not read for a native binary.",
        },

        new CliInstallDescriptor(
            ProviderName: "Gemini",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["gemini", "gemini-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, script only. @google/gemini-cli is a Node bundle (bundle/gemini.js, checked at 0.65.0); no native binary was found.",
        },

        new CliInstallDescriptor(
            ProviderName: "Junie",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["junie", "junie-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, not examined. The CLI is not an npm package in this registry.",
        },

        new CliInstallDescriptor(
            ProviderName: "Hermes",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["hermes", "hermes-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, not examined. The CLI is not an npm package in this registry.",
        },

        new CliInstallDescriptor(
            ProviderName: "Dsh",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["dsh", "dsh-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, not examined. The CLI is not an npm package in this registry.",
        },

        new CliInstallDescriptor(
            ProviderName: "Kimi",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["kimi", "kimi-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, not examined. The CLI is not an npm package in this registry.",
        },

        new CliInstallDescriptor(
            ProviderName: "Kiro",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["kiro-cli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, not examined. The CLI is not an npm package in this registry.",
        },

        new CliInstallDescriptor(
            ProviderName: "QoderCLI",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["qodercli"],
            IsPubliclyInstallable: false)
        {
            WindowsExecutableEvidence = "PreferNative, script only. @qoder-ai/qodercli is a Node bundle (bundle/qodercli.js, checked at 1.1.76); no native binary was found.",
        },

        new CliInstallDescriptor(
            ProviderName: "Reasonix",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["reasonix"],
            IsPubliclyInstallable: false)
        {
            // reasonix (2.47.1) is a Node script; @reasonix/cli-win32-x64 holds bin\reasonix.exe. No registry pin was available to check.
            WindowsExecutableEvidence =
                "PreferNative with a packaged layout. @reasonix/cli-win32-x64 holds bin\\reasonix.exe at 2.47.1 (the only version checked; there is no registry pin); "
                + "the main package is a Node script. The -win32-arm64 name comes from the optional dependencies. "
                + "The first npm publish of the platform package is 1.0.0-rc1 (2026-06-03); the oldest release that ships the .exe was not established.",
            PackagedBinaryLayout = new CliPackagedBinaryLayout(
                MainPackage: "reasonix",
                Targets:
                [
                    new CliPackagedBinaryTarget(Architecture.X64, "@reasonix/cli-win32-x64", "bin\\reasonix.exe"),
                    new CliPackagedBinaryTarget(Architecture.Arm64, "@reasonix/cli-win32-arm64", "bin\\reasonix.exe"),
                ]),
            InstallStrategies = [NpmStrategy()],
        },
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

    /// <summary>
    /// Finds the descriptor for a provider name, ignoring case.
    /// </summary>
    /// <param name="providerName">The provider name, such as <c>ClaudeCode</c>.</param>
    /// <returns>The descriptor, or <see langword="null" /> when the provider is not registered.</returns>
    public static CliInstallDescriptor? FindByProviderName(string providerName)
    {
        return Descriptors.FirstOrDefault(descriptor => string.Equals(descriptor.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
    }
}
