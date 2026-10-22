using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.CiSetup.Console;

/// <summary>
/// Runs the <c>CiSetup.Console</c> modes: the default npm install, strategy installs, the path-only verify, and discovery verification.
/// </summary>
public sealed class CiSetupRunner(CiSetupEnvironment environment)
{
    private const string ProviderFlag = "--provider";
    private const string StrategyFlag = "--strategy";

    private readonly CiSetupEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Runs the console with the supplied arguments.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The process exit code.</returns>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var parsed = CiSetupArgumentParser.Parse(args);

        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors)
            {
                _environment.Error.WriteLine($"  [FAIL] {error}");
            }

            return 1;
        }

        if (!parsed.Install && !parsed.Verify && !parsed.VerifyDiscovery)
        {
            WriteUsage();
            return 1;
        }

        if (parsed.Install)
        {
            var installExitCode = parsed.HasSelection
                ? await InstallSelectedAsync(parsed, cancellationToken).ConfigureAwait(false)
                : await InstallPublicAsync(cancellationToken).ConfigureAwait(false);
            if (installExitCode != 0)
            {
                return installExitCode;
            }
        }

        if (parsed.Verify)
        {
            var verifyExitCode = VerifyClis();
            if (verifyExitCode != 0 || !parsed.VerifyDiscovery)
            {
                return verifyExitCode;
            }
        }

        if (parsed.VerifyDiscovery)
        {
            return await VerifyDiscoveryAsync(parsed, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private void WriteUsage()
    {
        _environment.Error.WriteLine("Usage: CiSetup.Console [--install] [--verify]");
        _environment.Error.WriteLine("  --install  Install publicly available CLI tools via npm");
        _environment.Error.WriteLine("  --verify   Verify CLI tools are on PATH");
        _environment.Error.WriteLine("  --install --provider <name> --strategy <npm|native|homebrew|winget>");
        _environment.Error.WriteLine("             Install one provider through one install strategy");
        _environment.Error.WriteLine("  --verify-discovery [--provider <name>] [--strategy <kind>] [--expect-version <v>]");
        _environment.Error.WriteLine("                     [--expect-install-kind <kind>] [--expect-extension <.ext>] [--without-install-dir-on-path]");
        _environment.Error.WriteLine("             Resolve each CLI through discovery and assert path, source, kind, and version");
    }

    private async Task<int> InstallPublicAsync(CancellationToken cancellationToken)
    {
        _environment.Output.WriteLine("Installing publicly available CLI tools...");

        var npmExecutable = _environment.ResolveNpm();
        if (npmExecutable is null)
        {
            _environment.Error.WriteLine("  [FAIL] npm was not found on PATH. Ensure Node.js is installed before running CI setup.");
            return 1;
        }

        var hadErrors = false;
        foreach (var descriptor in CliInstallRegistry.PubliclyInstallable)
        {
            _environment.Output.WriteLine($"  [INSTALL] {descriptor.ProviderName}: {descriptor.FullPackageSpecifier}...");
            var result = await _environment.CommandRunner.RunAsync(
                new CliInstallCommand(
                    npmExecutable,
                    ["install", "--global", descriptor.FullPackageSpecifier],
                    new Dictionary<string, string?>()),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                _environment.Output.WriteLine($"  [OK] {descriptor.ProviderName} installed successfully");
            }
            else
            {
                _environment.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} installation failed (exit code {result.ExitCode})");
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    _environment.Error.WriteLine($"  {result.StandardError.Trim()}");
                }

                hadErrors = true;
            }
        }

        foreach (var descriptor in CliInstallRegistry.Descriptors.Where(static descriptor => !descriptor.IsPubliclyInstallable))
        {
            _environment.Output.WriteLine($"  [SKIP] {descriptor.ProviderName} is not publicly installable and must be validated with local setup.");
        }

        return hadErrors ? 1 : 0;
    }

    private async Task<int> InstallSelectedAsync(CiSetupArguments parsed, CancellationToken cancellationToken)
    {
        if (parsed.Provider is null)
        {
            _environment.Error.WriteLine($"  [FAIL] {StrategyFlag} requires {ProviderFlag}.");
            return 1;
        }

        var descriptor = FindDescriptor(parsed.Provider);
        if (descriptor is null)
        {
            _environment.Error.WriteLine($"  [FAIL] Unknown provider '{parsed.Provider}'.");
            return 1;
        }

        var strategyName = parsed.Strategy ?? "npm";
        if (!CiSetupStrategies.TryParseKind(strategyName, out var kind))
        {
            _environment.Error.WriteLine($"  [FAIL] Unknown strategy '{strategyName}' for {descriptor.ProviderName}.");
            return 1;
        }

        var platform = _environment.Platform;
        var strategy = descriptor.InstallStrategies.FirstOrDefault(candidate => candidate.Kind == kind && candidate.AppliesTo(platform));
        if (strategy is null)
        {
            _environment.Error.WriteLine($"  [FAIL] Strategy {strategyName} is not applicable to {descriptor.ProviderName} on {platform}.");
            return 1;
        }

        var installer = CiSetupStrategies.FindInstaller(kind);
        if (installer is null)
        {
            _environment.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} strategy {strategyName} has no installer in CiSetup.Console.");
            return 1;
        }

        string? npmExecutable = null;
        if (kind == CliInstallKind.Npm)
        {
            npmExecutable = _environment.ResolveNpm();
            if (npmExecutable is null)
            {
                _environment.Error.WriteLine("  [FAIL] npm was not found on PATH. Ensure Node.js is installed before running CI setup.");
                return 1;
            }
        }

        CliInstallCommand command;
        try
        {
            command = installer.CreateCommand(new CliInstallRequest(descriptor, strategy, platform, npmExecutable));
        }
        catch (ArgumentException exception)
        {
            _environment.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} via {strategyName}: {exception.Message}");
            return 1;
        }

        _environment.Output.WriteLine($"  [INSTALL] {descriptor.ProviderName} via {strategyName} on {platform}...");
        var result = await _environment.CommandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _environment.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} installation via {strategyName} failed (exit code {result.ExitCode}).");
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                _environment.Error.WriteLine($"  {detail.Trim()}");
            }

            return 1;
        }

        _environment.Output.WriteLine($"  [OK] {descriptor.ProviderName} installed via {strategyName}");
        return 0;
    }

    private int VerifyClis()
    {
        _environment.Output.WriteLine("Verifying CLI tool availability on PATH...");

        var resolver = new CliExecutableResolver();
        var hadFailures = false;

        foreach (var descriptor in CliInstallRegistry.PubliclyInstallable)
        {
            var path = resolver.ResolveProviderExecutable(descriptor.ProviderName, descriptor.ExecutableCandidates).ResolvedPath;
            if (path is not null)
            {
                _environment.Output.WriteLine($"  [OK] {descriptor.ProviderName} found at: {path} (candidates: {string.Join(", ", descriptor.ExecutableCandidates)})");
            }
            else
            {
                _environment.Error.WriteLine($"  [WARN] {descriptor.ProviderName} not found on PATH (candidates: {string.Join(", ", descriptor.ExecutableCandidates)})");
                hadFailures = true;
            }
        }

        foreach (var descriptor in CliInstallRegistry.Descriptors.Where(static descriptor => !descriptor.IsPubliclyInstallable))
        {
            _environment.Output.WriteLine($"  [SKIP] {descriptor.ProviderName} is outside the public CI install matrix.");
        }

        return hadFailures ? 1 : 0;
    }

    private async Task<int> VerifyDiscoveryAsync(CiSetupArguments parsed, CancellationToken cancellationToken)
    {
        if (parsed.Provider is null && (parsed.Strategy is not null || parsed.ExpectVersion is not null
                                        || parsed.ExpectInstallKind is not null || parsed.ExpectExtension is not null
                                        || parsed.WithoutInstallDirOnPath))
        {
            _environment.Error.WriteLine($"  [FAIL] --strategy, --expect-version, --expect-install-kind, --expect-extension, and --without-install-dir-on-path require {ProviderFlag}.");
            return 1;
        }

        CliInstallKind? strategyKind = null;
        if (parsed.Strategy is not null)
        {
            if (!CiSetupStrategies.TryParseKind(parsed.Strategy, out var parsedKind))
            {
                _environment.Error.WriteLine($"  [FAIL] Unknown strategy '{parsed.Strategy}'.");
                return 1;
            }

            strategyKind = parsedKind;
        }

        if (parsed.WithoutInstallDirOnPath && strategyKind is null)
        {
            _environment.Error.WriteLine($"  [FAIL] --without-install-dir-on-path requires {StrategyFlag}.");
            return 1;
        }

        CliInstallKind? explicitKind = null;
        if (parsed.ExpectInstallKind is not null)
        {
            if (!CiSetupStrategies.TryParseKind(parsed.ExpectInstallKind, out var parsedExpectedKind))
            {
                _environment.Error.WriteLine($"  [FAIL] Unknown install kind '{parsed.ExpectInstallKind}'.");
                return 1;
            }

            explicitKind = parsedExpectedKind;
        }

        IReadOnlyList<CliInstallDescriptor> targets;
        if (parsed.Provider is not null)
        {
            var descriptor = FindDescriptor(parsed.Provider);
            if (descriptor is null)
            {
                _environment.Error.WriteLine($"  [FAIL] Unknown provider '{parsed.Provider}'.");
                return 1;
            }

            targets = [descriptor];
        }
        else
        {
            targets = CliInstallRegistry.PubliclyInstallable;
            foreach (var skipped in CliInstallRegistry.Descriptors.Where(static descriptor => !descriptor.IsPubliclyInstallable))
            {
                _environment.Output.WriteLine($"  [SKIP] {skipped.ProviderName} is outside the public CI install matrix.");
            }
        }

        var overallExitCode = 0;
        foreach (var descriptor in targets)
        {
            var exitCode = await VerifyOneDiscoveryAsync(descriptor, parsed, strategyKind, explicitKind, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0 && overallExitCode == 0)
            {
                overallExitCode = exitCode;
            }
        }

        return overallExitCode;
    }

    private async Task<int> VerifyOneDiscoveryAsync(
        CliInstallDescriptor descriptor,
        CiSetupArguments parsed,
        CliInstallKind? strategyKind,
        CliInstallKind? explicitKind,
        CancellationToken cancellationToken)
    {
        var platform = _environment.Platform;
        CliInstallStrategy? strategy = null;
        if (strategyKind is { } kind)
        {
            if (!descriptor.InstallStrategies.Any(candidate => candidate.Kind == kind))
            {
                _environment.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} declares no {parsed.Strategy} strategy.");
                return 1;
            }

            strategy = descriptor.InstallStrategies.FirstOrDefault(candidate => candidate.Kind == kind && candidate.AppliesTo(platform));
            if (strategy is null)
            {
                _environment.Error.WriteLine($"  [FAIL] Strategy {parsed.Strategy} is not applicable to {descriptor.ProviderName} on {platform}.");
                return 1;
            }
        }

        var hostEnvironment = _environment.HostEnvironment;
        var strategyDirectories = strategy?.GetWellKnownDirectories(platform, hostEnvironment) ?? [];
        var discoveryEnvironment = parsed.WithoutInstallDirOnPath
            ? RemoveDirectoriesFromPath(hostEnvironment, strategyDirectories, platform)
            : hostEnvironment;

        _environment.Output.WriteLine(
            $"Discovering {descriptor.ProviderName} (strategy: {parsed.Strategy ?? "any"}, platform: {platform}, install directory on PATH: {(parsed.WithoutInstallDirOnPath ? "no" : "yes")})...");

        var result = await _environment.Discover(
            descriptor,
            new CliDiscoveryOptions { EnvironmentVariables = discoveryEnvironment },
            cancellationToken).ConfigureAwait(false);

        // The expected install kind defaults to the strategy's kind, so a lane cannot pass on a different install.
        var expectedKind = explicitKind ?? strategy?.Kind;
        var expectations = new CiSetupExpectations(
            parsed.ExpectVersion,
            expectedKind,
            RequireWellKnownSource: parsed.WithoutInstallDirOnPath,
            ExpectedExtension: parsed.ExpectExtension);
        var verification = CiSetupDiscoveryVerifier.Evaluate(descriptor, strategy, expectations, result);

        if (result.IsFound)
        {
            _environment.Output.WriteLine($"  path: {result.ResolvedPath}");
            _environment.Output.WriteLine($"  source: {result.Source}");
            _environment.Output.WriteLine($"  install kind: {result.InstallKind}");
            _environment.Output.WriteLine($"  version: {result.Version ?? "(unavailable)"}");
        }

        foreach (var note in verification.Notes)
        {
            _environment.Output.WriteLine($"  [INFO] {note}");
        }

        if (verification.Verdict == CiSetupVerdict.Passed)
        {
            _environment.Output.WriteLine($"  [OK] {descriptor.ProviderName} {verification.Summary}");
            return 0;
        }

        _environment.Error.WriteLine($"  [FAIL] {descriptor.ProviderName} {verification.Summary}");
        WriteDiagnostics(result, strategyDirectories);
        return verification.ExitCode;
    }

    private void WriteDiagnostics(CliDiscoveryResult result, IReadOnlyList<string> strategyDirectories)
    {
        _environment.Error.WriteLine("  probed locations:");
        foreach (var location in result.ProbedLocations)
        {
            _environment.Error.WriteLine($"    {location}");
        }

        foreach (var directory in strategyDirectories)
        {
            if (!Directory.Exists(directory))
            {
                _environment.Error.WriteLine($"  strategy directory {directory}: (missing)");
                continue;
            }

            var entries = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName);
            _environment.Error.WriteLine($"  strategy directory {directory}: {string.Join(", ", entries)}");
        }
    }

    private CliInstallDescriptor? FindDescriptor(string providerName)
    {
        return CliInstallRegistry.Descriptors.FirstOrDefault(descriptor =>
            string.Equals(descriptor.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyDictionary<string, string?> RemoveDirectoriesFromPath(
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<string> directories,
        CliPlatform platform)
    {
        var comparer = platform == CliPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var pathKey = environment.Keys.FirstOrDefault(key => comparer.Equals(key, "PATH")) ?? "PATH";
        var pathValue = environment.TryGetValue(pathKey, out var value) ? value : null;

        var remove = new HashSet<string>(
            directories.Select(static directory => directory.TrimEnd('/', '\\')),
            comparer);
        var kept = (pathValue ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !remove.Contains(entry.TrimEnd('/', '\\')))
            .ToArray();

        var updated = new Dictionary<string, string?>(environment, comparer)
        {
            [pathKey] = string.Join(Path.PathSeparator, kept)
        };
        return updated;
    }
}
