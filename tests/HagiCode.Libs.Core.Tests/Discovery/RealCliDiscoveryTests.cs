using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

/// <summary>
/// Real-install discovery checks. They run only when HAGICODE_REAL_CLI_TESTS is 1 or true, so the default suite starts no CLI.
/// HAGICODE_REAL_CLI_PROVIDER selects the descriptor (default ClaudeCode). HAGICODE_REAL_CLI_STRATEGY selects the expected install route.
/// </summary>
public sealed class RealCliDiscoveryTests
{
    private const string EnableVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string ProviderVariable = "HAGICODE_REAL_CLI_PROVIDER";
    private const string StrategyVariable = "HAGICODE_REAL_CLI_STRATEGY";
    private const string DefaultProvider = "ClaudeCode";

    private static readonly Regex WellFormedVersion = new(
        @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.+-]+)?$",
        RegexOptions.CultureInvariant);

    [Test]
    [Category("External")]
    [Category("RealCli")]
    [Category("RealCliDiscovery")]
    public async Task Resolves_the_installed_cli_with_its_install_directory_on_path()
    {
        ExternalGate.Require(EnableVariable);
        var (descriptor, strategy) = ResolveExpectation();

        var result = await DiscoverAsync(descriptor, CurrentEnvironment());

        AssertFoundWithExpectations(descriptor, strategy, result);
        (result.Source is CliDiscoverySource.Path or CliDiscoverySource.WellKnownLocation).ShouldBeTrue(
            $"Expected the install directory to be on PATH, but the source was {result.Source}.");
    }

    [Test]
    [Category("External")]
    [Category("RealCli")]
    [Category("RealCliDiscovery")]
    public async Task Resolves_the_cli_through_its_well_known_location_when_its_directory_is_off_path()
    {
        ExternalGate.Require(EnableVariable);
        var (descriptor, strategy) = ResolveExpectation();
        if (strategy is null)
        {
            Assert.Ignore($"Set {StrategyVariable} to a strategy that declares a well-known location to run the off-PATH check.");
        }

        var environment = RemoveStrategyDirectoriesFromPath(CurrentEnvironment(), strategy!);

        var result = await DiscoverAsync(descriptor, environment);

        AssertFoundWithExpectations(descriptor, strategy, result);
        result.Source.ShouldBe(CliDiscoverySource.WellKnownLocation);
    }

    private static (CliInstallDescriptor Descriptor, CliInstallStrategy? Strategy) ResolveExpectation()
    {
        var providerName = System.Environment.GetEnvironmentVariable(ProviderVariable);
        if (string.IsNullOrWhiteSpace(providerName))
        {
            providerName = DefaultProvider;
        }

        var descriptor = CliInstallRegistry.Descriptors.Single(candidate =>
            string.Equals(candidate.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));

        var kind = ParseStrategy(System.Environment.GetEnvironmentVariable(StrategyVariable));
        var strategy = kind is null
            ? null
            : descriptor.InstallStrategies.FirstOrDefault(candidate => candidate.Kind == kind && candidate.AppliesTo(CurrentPlatform()))
              ?? throw new InvalidOperationException($"{descriptor.ProviderName} declares no {kind} strategy for {CurrentPlatform()}.");
        return (descriptor, strategy);
    }

    private static async Task<CliDiscoveryResult> DiscoverAsync(CliInstallDescriptor descriptor, IReadOnlyDictionary<string, string?> environment)
    {
        var service = new CliDiscoveryService(new CliExecutableResolver(), new CliProcessManager());
        return await service.DiscoverAsync(descriptor, new CliDiscoveryOptions { EnvironmentVariables = environment });
    }

    private static void AssertFoundWithExpectations(CliInstallDescriptor descriptor, CliInstallStrategy? strategy, CliDiscoveryResult result)
    {
        result.IsFound.ShouldBeTrue($"{descriptor.ProviderName} was not found. Probed: {string.Join(", ", result.ProbedLocations)}");
        File.Exists(result.ResolvedPath).ShouldBeTrue();
        result.VersionFailure.ShouldBe(CliVersionProbeFailure.None);

        if (strategy is not null)
        {
            result.InstallKind.ShouldBe(strategy.Kind);
        }

        if (strategy is { SupportsVersionPin: true } && !string.IsNullOrWhiteSpace(descriptor.PinnedVersion))
        {
            result.Version.ShouldBe(descriptor.PinnedVersion);
        }
        else
        {
            result.Version.ShouldNotBeNull();
            WellFormedVersion.IsMatch(result.Version!).ShouldBeTrue($"Version '{result.Version}' is not well formed.");
        }
    }

    private static IReadOnlyDictionary<string, string?> CurrentEnvironment()
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

    private static IReadOnlyDictionary<string, string?> RemoveStrategyDirectoriesFromPath(
        IReadOnlyDictionary<string, string?> environment,
        CliInstallStrategy strategy)
    {
        var directories = strategy.GetWellKnownDirectories(CurrentPlatform(), environment)
            .Select(static directory => directory.TrimEnd('/', '\\'))
            .ToHashSet(StringComparer.Ordinal);
        var pathValue = environment.TryGetValue("PATH", out var value) ? value ?? string.Empty : string.Empty;
        var kept = pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !directories.Contains(entry.TrimEnd('/', '\\')));

        var updated = new Dictionary<string, string?>(environment, StringComparer.Ordinal)
        {
            ["PATH"] = string.Join(Path.PathSeparator, kept)
        };
        return updated;
    }

    private static CliPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return CliPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? CliPlatform.MacOS : CliPlatform.Linux;
    }

    private static CliInstallKind? ParseStrategy(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "npm" => CliInstallKind.Npm,
            "native" or "nativeinstaller" or "native-installer" => CliInstallKind.NativeInstaller,
            "homebrew" or "brew" => CliInstallKind.Homebrew,
            "winget" => CliInstallKind.WinGet,
            "legacy" or "legacylocal" or "legacy-local" => CliInstallKind.LegacyLocal,
            _ => throw new InvalidOperationException($"Unknown {StrategyVariable} value '{value}'.")
        };
    }
}
