using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.DeepAgents;

internal sealed class DeepAgentsLaunchResolver
{
    private static readonly string[] DirectExecutableCandidates = ["deepagents"];
    private static readonly string[] UvxExecutableCandidates = ["uvx", "uvx.cmd", "uvx.exe", "uvx.bat"];
    private const string BootstrapArgument = "--acp";
    private const string UvxFromArgument = "--from";
    private const string UvxPackageName = "deepagents-cli";
    private const string DeepAgentsCommandName = "deepagents";

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
                : CreateManagedLauncher(
                    resolvedExplicitPath,
                    managedArguments,
                    resolvedExplicitPath,
                    UsesFallbackLauncher: false);
        }

        var directExecutable = _executableResolver.ResolveFirstAvailablePath(DirectExecutableCandidates, runtimeEnvironment);
        if (directExecutable is not null)
        {
            return CreateManagedLauncher(directExecutable, managedArguments, "deepagents --acp", UsesFallbackLauncher: false);
        }

        var uvxExecutable = _executableResolver.ResolveFirstAvailablePath(UvxExecutableCandidates, runtimeEnvironment);
        if (uvxExecutable is null)
        {
            return null;
        }

        var launchArguments = new List<string>(managedArguments.Count + 4)
        {
            UvxFromArgument,
            UvxPackageName,
            DeepAgentsCommandName,
            BootstrapArgument
        };
        launchArguments.AddRange(managedArguments);
        return new DeepAgentsManagedLauncher(uvxExecutable, launchArguments, "uvx --from deepagents-cli deepagents --acp", UsesFallbackLauncher: true);
    }

    private static DeepAgentsManagedLauncher CreateManagedLauncher(
        string executablePath,
        IReadOnlyList<string> managedArguments,
        string displayName,
        bool UsesFallbackLauncher)
    {
        var launchArguments = new List<string>(managedArguments.Count + 1) { BootstrapArgument };
        launchArguments.AddRange(managedArguments);

        return new DeepAgentsManagedLauncher(executablePath, launchArguments, displayName, UsesFallbackLauncher);
    }
}

internal sealed record DeepAgentsManagedLauncher(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string DisplayName,
    bool UsesFallbackLauncher);
