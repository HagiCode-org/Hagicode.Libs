using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.DeepAgents;

internal sealed class DeepAgentsLaunchResolver
{
    private static readonly string[] DirectExecutableCandidates = ["deepagents-acp"];
    private static readonly string[] NpxExecutableCandidates = ["npx", "npx.cmd", "npx.exe", "npx.bat"];
    private const string NpmPackageName = "deepagents-acp";

    private readonly CliExecutableResolver _executableResolver;

    public DeepAgentsLaunchResolver(CliExecutableResolver executableResolver)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
    }

    public DeepAgentsManagedLauncher? Resolve(
        DeepAgentsOptions options,
        IReadOnlyList<string> managedArguments,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(managedArguments);
        ArgumentNullException.ThrowIfNull(runtimeEnvironment);

        var explicitExecutable = ArgumentValueNormalizer.NormalizeOptionalValue(options.ExecutablePath);
        if (explicitExecutable is not null)
        {
            var resolvedExplicitPath = _executableResolver.ResolveExecutablePath(explicitExecutable, runtimeEnvironment);
            return resolvedExplicitPath is null
                ? null
                : new DeepAgentsManagedLauncher(resolvedExplicitPath, managedArguments, resolvedExplicitPath, UsesNpxFallback: false);
        }

        var directExecutable = _executableResolver.ResolveFirstAvailablePath(DirectExecutableCandidates, runtimeEnvironment);
        if (directExecutable is not null)
        {
            return new DeepAgentsManagedLauncher(directExecutable, managedArguments, "deepagents-acp", UsesNpxFallback: false);
        }

        var npxExecutable = _executableResolver.ResolveFirstAvailablePath(NpxExecutableCandidates, runtimeEnvironment);
        if (npxExecutable is null)
        {
            return null;
        }

        var launchArguments = new List<string>(managedArguments.Count + 1) { NpmPackageName };
        launchArguments.AddRange(managedArguments);
        return new DeepAgentsManagedLauncher(npxExecutable, launchArguments, "npx deepagents-acp", UsesNpxFallback: true);
    }
}

internal sealed record DeepAgentsManagedLauncher(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string DisplayName,
    bool UsesNpxFallback);
