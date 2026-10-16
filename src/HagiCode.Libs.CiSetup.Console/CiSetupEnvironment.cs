using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.CiSetup.Console;

/// <summary>
/// Runs discovery for one descriptor. Matches <see cref="CliDiscoveryService.DiscoverAsync" />.
/// </summary>
public delegate Task<CliDiscoveryResult> CiSetupDiscoveryDelegate(
    CliInstallDescriptor descriptor,
    CliDiscoveryOptions options,
    CancellationToken cancellationToken);

/// <summary>
/// The side effects <see cref="CiSetupRunner" /> depends on. Tests replace each one.
/// </summary>
/// <param name="Output">Standard output writer.</param>
/// <param name="Error">Standard error writer.</param>
/// <param name="CommandRunner">Runs install commands.</param>
/// <param name="Discover">Runs discovery.</param>
/// <param name="Platform">The platform the console runs on.</param>
/// <param name="HostEnvironment">The host environment, used for discovery and PATH edits.</param>
/// <param name="ResolveNpm">Returns the npm executable path, or <see langword="null" /> when npm is not installed.</param>
public sealed record CiSetupEnvironment(
    TextWriter Output,
    TextWriter Error,
    ICiSetupCommandRunner CommandRunner,
    CiSetupDiscoveryDelegate Discover,
    CliPlatform Platform,
    IReadOnlyDictionary<string, string?> HostEnvironment,
    Func<string?> ResolveNpm)
{
    /// <summary>
    /// Creates the environment used by the real console.
    /// </summary>
    /// <param name="output">Standard output writer.</param>
    /// <param name="error">Standard error writer.</param>
    /// <returns>The production environment.</returns>
    public static CiSetupEnvironment CreateDefault(TextWriter output, TextWriter error)
    {
        var discovery = new CliDiscoveryService(new CliExecutableResolver(), new CliProcessManager());
        return new CiSetupEnvironment(
            output,
            error,
            new ProcessCiSetupCommandRunner(new CliProcessManager()),
            (descriptor, options, cancellationToken) => discovery.DiscoverAsync(descriptor, options, cancellationToken),
            CurrentPlatform(),
            ReadProcessEnvironment(),
            static () => new CliExecutableResolver().ResolveFirstAvailablePath(["npm", "npm.cmd", "npm.exe", "npm.bat"]));
    }

    private static CliPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return CliPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? CliPlatform.MacOS : CliPlatform.Linux;
    }

    private static IReadOnlyDictionary<string, string?> ReadProcessEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                environment[key] = entry.Value as string;
            }
        }

        return environment;
    }
}
