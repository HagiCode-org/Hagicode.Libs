using System.Text.RegularExpressions;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Identifies how a CLI executable was installed. <see cref="Unknown" /> means attribution was inconclusive.
/// </summary>
public enum CliInstallKind
{
    /// <summary>The install route could not be attributed.</summary>
    Unknown,

    /// <summary>Installed with <c>npm install --global</c>.</summary>
    Npm,

    /// <summary>Installed with a vendor native installer that places a launcher in a user-level directory.</summary>
    NativeInstaller,

    /// <summary>Installed with Homebrew on macOS.</summary>
    Homebrew,

    /// <summary>Installed with WinGet on Windows.</summary>
    WinGet,

    /// <summary>Installed with the legacy local installer under the user's home directory.</summary>
    LegacyLocal
}

/// <summary>
/// Operating systems a discovery strategy applies to. Flags may be combined.
/// </summary>
[Flags]
public enum CliPlatform
{
    /// <summary>No platform.</summary>
    None = 0,

    /// <summary>Linux.</summary>
    Linux = 1,

    /// <summary>macOS.</summary>
    MacOS = 2,

    /// <summary>Windows.</summary>
    Windows = 4,

    /// <summary>Linux and macOS.</summary>
    Unix = Linux | MacOS,

    /// <summary>Every supported platform.</summary>
    All = Linux | MacOS | Windows
}

/// <summary>
/// Describes one install route for a CLI: where its launcher usually lands and whether the route can pin a version.
/// Discovery metadata only. Installer commands belong to tooling, not to this package.
/// </summary>
/// <param name="Kind">The install route.</param>
/// <param name="Platforms">The operating systems on which the route applies.</param>
/// <param name="WellKnownDirectories">
/// Directory templates. <c>~</c> expands to the home directory (<c>HOME</c>, or <c>USERPROFILE</c> on Windows).
/// <c>%NAME%</c> expands to the named environment variable. A template with an unresolved token is skipped.
/// Both <c>/</c> and <c>\</c> are accepted and normalized to the platform separator.
/// </param>
/// <param name="SupportsVersionPin">Whether the route can install a chosen version.</param>
public sealed record CliInstallStrategy(
    CliInstallKind Kind,
    CliPlatform Platforms,
    IReadOnlyList<string> WellKnownDirectories,
    bool SupportsVersionPin)
{
    /// <summary>
    /// Determines whether this strategy applies to a platform.
    /// </summary>
    /// <param name="platform">The platform to test.</param>
    /// <returns><see langword="true" /> when the strategy declares that platform; otherwise <see langword="false" />.</returns>
    public bool AppliesTo(CliPlatform platform) => (Platforms & platform) != CliPlatform.None;

    /// <summary>
    /// Expands this strategy's directory templates for one platform, reading variables from the supplied environment.
    /// </summary>
    /// <param name="platform">The platform to expand for. Strategies that do not apply yield no directories.</param>
    /// <param name="environment">The environment that supplies home and other variables. The host environment is not read.</param>
    /// <returns>The resolved directories in declaration order. Templates with unresolved tokens are skipped.</returns>
    public IReadOnlyList<string> GetWellKnownDirectories(CliPlatform platform, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!AppliesTo(platform))
        {
            return [];
        }

        return CliInstallDirectoryExpander.Expand(
            WellKnownDirectories,
            platform == CliPlatform.Windows,
            environment);
    }
}

/// <summary>
/// Expands well-known directory templates from a supplied environment.
/// </summary>
internal static class CliInstallDirectoryExpander
{
    private static readonly Regex VariableTokenRegex = new("%(?<name>[^%]+)%", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> Expand(
        IEnumerable<string> templates,
        bool isWindows,
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(environment);

        // Windows variable names are case-insensitive, so the lookup copy follows the platform rule.
        var variables = new Dictionary<string, string?>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in environment)
        {
            variables[entry.Key] = entry.Value;
        }

        var expanded = new List<string>();
        foreach (var template in templates)
        {
            if (TryExpand(template, isWindows, variables, out var directory))
            {
                expanded.Add(directory);
            }
        }

        return expanded;
    }

    private static bool TryExpand(
        string template,
        bool isWindows,
        IReadOnlyDictionary<string, string?> variables,
        out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        var text = template;
        if (text.StartsWith('~') && (text.Length == 1 || text[1] is '/' or '\\'))
        {
            var home = GetVariable(variables, isWindows ? "USERPROFILE" : "HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                return false;
            }

            text = home + text[1..];
        }

        var hasUnresolvedToken = false;
        text = VariableTokenRegex.Replace(text, match =>
        {
            var value = GetVariable(variables, match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                hasUnresolvedToken = true;
                return match.Value;
            }

            return value;
        });

        if (hasUnresolvedToken || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        directory = isWindows
            ? text.Replace('/', '\\')
            : text.Replace('\\', '/');
        return true;
    }

    private static string? GetVariable(IReadOnlyDictionary<string, string?> variables, string name)
    {
        return variables.TryGetValue(name, out var value) ? value : null;
    }
}
