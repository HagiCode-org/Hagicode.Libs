using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.CiSetup.Console;

/// <summary>
/// A command the installer runs, expressed as data so tests can inspect it without starting a process.
/// </summary>
/// <param name="FileName">The executable name or path.</param>
/// <param name="Arguments">The argument list.</param>
/// <param name="EnvironmentVariables">Environment overrides for the installer process.</param>
public sealed record CliInstallCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentVariables);

/// <summary>
/// The inputs a strategy installer needs to build its command.
/// </summary>
/// <param name="Descriptor">The provider descriptor.</param>
/// <param name="Strategy">The registry strategy being installed.</param>
/// <param name="Platform">The current platform.</param>
/// <param name="NpmExecutable">The resolved npm executable, when one exists.</param>
public sealed record CliInstallRequest(
    CliInstallDescriptor Descriptor,
    CliInstallStrategy Strategy,
    CliPlatform Platform,
    string? NpmExecutable)
{
    /// <summary>
    /// Gets the version to request, or <see langword="null" /> when the strategy cannot pin or the descriptor has no pin.
    /// </summary>
    public string? PinnedVersion => Strategy.SupportsVersionPin && !string.IsNullOrWhiteSpace(Descriptor.PinnedVersion)
        ? Descriptor.PinnedVersion
        : null;
}

/// <summary>
/// Builds the install command for one install route. Lives in the console project, which is not packaged.
/// </summary>
public interface ICliStrategyInstaller
{
    /// <summary>Gets the install route this installer serves.</summary>
    CliInstallKind Kind { get; }

    /// <summary>Builds the command for a request.</summary>
    /// <param name="request">The install request.</param>
    /// <returns>The command to run.</returns>
    CliInstallCommand CreateCommand(CliInstallRequest request);
}

/// <summary>
/// Runs install commands and reports the result.
/// </summary>
public interface ICiSetupCommandRunner
{
    /// <summary>Runs a command to completion.</summary>
    /// <param name="command">The command to run.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The exit code and captured output.</returns>
    Task<CiSetupCommandResult> RunAsync(CliInstallCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of one command.
/// </summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record CiSetupCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// The built-in strategy installers and the name mapping for <c>--strategy</c>.
/// </summary>
public static class CiSetupStrategies
{
    /// <summary>
    /// Environment set for every installer process so a native install does not update itself mid-lane.
    /// </summary>
    public const string DisableAutoUpdaterVariable = "DISABLE_AUTOUPDATER";

    /// <summary>
    /// The Claude Code native installer script for Unix shells.
    /// </summary>
    public const string NativeUnixInstallerUrl = "https://claude.ai/install.sh";

    /// <summary>
    /// The Claude Code native installer script for Windows PowerShell.
    /// </summary>
    public const string NativeWindowsInstallerUrl = "https://claude.ai/install.ps1";

    /// <summary>
    /// The Homebrew cask for Claude Code.
    /// </summary>
    public const string HomebrewCask = "claude-code";

    /// <summary>
    /// The WinGet package identifier for Claude Code.
    /// </summary>
    public const string WinGetPackageId = "Anthropic.ClaudeCode";

    /// <summary>
    /// Gets the installers that this console knows how to run.
    /// </summary>
    public static IReadOnlyList<ICliStrategyInstaller> Installers { get; } =
    [
        new NpmStrategyInstaller(),
        new NativeInstallerStrategyInstaller(),
        new HomebrewStrategyInstaller(),
        new WinGetStrategyInstaller(),
    ];

    /// <summary>
    /// Parses a <c>--strategy</c> or <c>--expect-install-kind</c> value.
    /// </summary>
    /// <param name="value">The user-supplied name.</param>
    /// <param name="kind">The matching install kind.</param>
    /// <returns><see langword="true" /> when the name is known; otherwise <see langword="false" />.</returns>
    public static bool TryParseKind(string? value, out CliInstallKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "npm":
                kind = CliInstallKind.Npm;
                return true;
            case "native":
            case "nativeinstaller":
            case "native-installer":
                kind = CliInstallKind.NativeInstaller;
                return true;
            case "homebrew":
            case "brew":
                kind = CliInstallKind.Homebrew;
                return true;
            case "winget":
                kind = CliInstallKind.WinGet;
                return true;
            case "legacy":
            case "legacylocal":
            case "legacy-local":
                kind = CliInstallKind.LegacyLocal;
                return true;
            default:
                kind = CliInstallKind.Unknown;
                return false;
        }
    }

    /// <summary>
    /// Finds the installer for an install kind.
    /// </summary>
    /// <param name="kind">The install kind.</param>
    /// <returns>The installer, or <see langword="null" /> when none is implemented (for example, legacy local).</returns>
    public static ICliStrategyInstaller? FindInstaller(CliInstallKind kind) =>
        Installers.FirstOrDefault(installer => installer.Kind == kind);

    /// <summary>
    /// Builds the environment for an installer process.
    /// </summary>
    /// <returns>The overrides that every installer applies.</returns>
    public static IReadOnlyDictionary<string, string?> InstallerEnvironment() =>
        new Dictionary<string, string?>(StringComparer.Ordinal) { [DisableAutoUpdaterVariable] = "1" };

    /// <summary>
    /// Returns <see langword="true" /> when a version string is a plain semantic version safe to place in a command line.
    /// </summary>
    /// <param name="version">The version string.</param>
    public static bool IsSafeVersion(string? version) =>
        version is not null && System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.+-]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

/// <summary>
/// Installs a CLI with the npm global route.
/// </summary>
public sealed class NpmStrategyInstaller : ICliStrategyInstaller
{
    /// <inheritdoc />
    public CliInstallKind Kind => CliInstallKind.Npm;

    /// <inheritdoc />
    public CliInstallCommand CreateCommand(CliInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var package = request.PinnedVersion is { } version
            ? $"{request.Descriptor.NpmPackage}@{version}"
            : request.Descriptor.NpmPackage;
        return new CliInstallCommand(
            request.NpmExecutable ?? "npm",
            ["install", "--global", package],
            CiSetupStrategies.InstallerEnvironment());
    }
}

/// <summary>
/// Installs a CLI with its vendor native installer script.
/// </summary>
public sealed class NativeInstallerStrategyInstaller : ICliStrategyInstaller
{
    /// <inheritdoc />
    public CliInstallKind Kind => CliInstallKind.NativeInstaller;

    /// <inheritdoc />
    public CliInstallCommand CreateCommand(CliInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var version = request.PinnedVersion;
        if (version is not null && !CiSetupStrategies.IsSafeVersion(version))
        {
            throw new ArgumentException($"Pinned version '{version}' is not a plain semantic version.", nameof(request));
        }

        if (request.Platform == CliPlatform.Windows)
        {
            var versionArgument = version is null ? string.Empty : " " + version;
            return new CliInstallCommand(
                "powershell",
                [
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-Command",
                    $"& ([scriptblock]::Create((Invoke-RestMethod {CiSetupStrategies.NativeWindowsInstallerUrl}))){versionArgument}"
                ],
                CiSetupStrategies.InstallerEnvironment());
        }

        var unixVersionArgument = version is null ? string.Empty : " " + version;
        return new CliInstallCommand(
            "bash",
            ["-o", "pipefail", "-c", $"curl -fsSL {CiSetupStrategies.NativeUnixInstallerUrl} | bash -s{unixVersionArgument}"],
            CiSetupStrategies.InstallerEnvironment());
    }
}

/// <summary>
/// Installs a CLI with the Homebrew cask on macOS.
/// </summary>
public sealed class HomebrewStrategyInstaller : ICliStrategyInstaller
{
    /// <inheritdoc />
    public CliInstallKind Kind => CliInstallKind.Homebrew;

    /// <inheritdoc />
    public CliInstallCommand CreateCommand(CliInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CliInstallCommand(
            "brew",
            ["install", "--cask", CiSetupStrategies.HomebrewCask],
            CiSetupStrategies.InstallerEnvironment());
    }
}

/// <summary>
/// Installs a CLI with WinGet on Windows.
/// </summary>
public sealed class WinGetStrategyInstaller : ICliStrategyInstaller
{
    /// <inheritdoc />
    public CliInstallKind Kind => CliInstallKind.WinGet;

    /// <inheritdoc />
    public CliInstallCommand CreateCommand(CliInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string>
        {
            "install",
            "--id", CiSetupStrategies.WinGetPackageId,
            "--exact",
            "--accept-source-agreements",
            "--accept-package-agreements",
            "--disable-interactivity"
        };
        if (request.PinnedVersion is { } version)
        {
            arguments.Add("--version");
            arguments.Add(version);
        }

        return new CliInstallCommand("winget", arguments, CiSetupStrategies.InstallerEnvironment());
    }
}
