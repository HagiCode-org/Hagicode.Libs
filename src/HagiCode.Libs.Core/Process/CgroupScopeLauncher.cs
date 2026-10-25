using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Puts an agent CLI launch inside a memory-limited systemd user scope. Every launch path calls this one helper after it
/// has finished its own <see cref="ProcessStartInfo" />, so every path produces identical limits.
/// </summary>
/// <remarks>
/// <c>systemd-run --scope</c> registers the scope and then replaces itself with the CLI, so the started process id is the
/// CLI's id and the standard streams and exit code pass through unchanged.
/// </remarks>
public static class CgroupScopeLauncher
{
    internal const string NodeOptionsVariableName = "NODE_OPTIONS";
    private const string NodeHeapOptionName = "--max-old-space-size";

    /// <summary>
    /// Wraps <paramref name="startInfo" /> when <paramref name="limitProvider" /> reports a limit and the shared
    /// <see cref="CgroupScopeCapabilityProbe.Default" /> says the host can enforce it.
    /// </summary>
    /// <param name="startInfo">The finished start information. It is changed in place.</param>
    /// <param name="limitProvider">The limit source, or <see langword="null" /> for no limit.</param>
    /// <param name="logger">Receives one warning per unsupported reason.</param>
    /// <returns><see langword="true" /> when the start information was wrapped.</returns>
    public static bool Apply(ProcessStartInfo startInfo, ICliResourceLimitProvider? limitProvider, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return Apply(startInfo, limitProvider?.GetCurrent(), CgroupScopeCapabilityProbe.Default, logger);
    }

    /// <summary>
    /// Wraps <paramref name="startInfo" /> behind <c>systemd-run --user --scope --quiet -p ... --</c> and adds the Node.js heap
    /// option to <c>NODE_OPTIONS</c>. Without a limit nothing changes. When the host cannot enforce the limit, nothing changes
    /// either (fail open), and one warning is logged per reason for the lifetime of <paramref name="probe" />.
    /// </summary>
    /// <param name="startInfo">The finished start information. It is changed in place.</param>
    /// <param name="limits">The limit, or <see langword="null" /> for no limit.</param>
    /// <param name="probe">The host capability probe.</param>
    /// <param name="logger">Receives one warning per unsupported reason.</param>
    /// <returns><see langword="true" /> when the start information was wrapped.</returns>
    public static bool Apply(ProcessStartInfo startInfo, CliResourceLimits? limits, CgroupScopeCapabilityProbe probe, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(probe);

        if (limits is null)
        {
            return false;
        }

        var capability = probe.GetCapability();
        if (!capability.Supported || string.IsNullOrWhiteSpace(capability.SystemdRunPath))
        {
            if (probe.TryMarkWarned(capability.Reason))
            {
                (logger ?? NullLogger.Instance).LogWarning(
                    "Agent CLI memory limit of {LimitMegabytes} MB is not enforced on this host ({Reason}); agent CLIs start without it.",
                    limits.MemoryMaxMegabytes,
                    capability.ReasonCode);
            }

            return false;
        }

        // Only the Windows cmd.exe launch uses the single argument string, and that path never reaches a supported (Linux) host.
        if (!string.IsNullOrEmpty(startInfo.Arguments) || string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            return false;
        }

        var executablePath = startInfo.FileName;
        var arguments = startInfo.ArgumentList.ToArray();

        startInfo.FileName = capability.SystemdRunPath;
        startInfo.ArgumentList.Clear();
        foreach (var argument in limits.SystemdRunArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(executablePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.TryGetValue(NodeOptionsVariableName, out var nodeOptions);
        startInfo.Environment[NodeOptionsVariableName] = MergeNodeOptions(nodeOptions, limits);
        return true;
    }

    /// <summary>
    /// Adds the heap option to existing <c>NODE_OPTIONS</c> content. A caller's own <c>--max-old-space-size</c> is kept as it is.
    /// </summary>
    internal static string MergeNodeOptions(string? nodeOptions, CliResourceLimits limits)
    {
        if (string.IsNullOrWhiteSpace(nodeOptions))
        {
            return limits.NodeHeapOption;
        }

        // Node.js accepts the option with dashes or underscores.
        if (nodeOptions.Replace('_', '-').Contains(NodeHeapOptionName, StringComparison.Ordinal))
        {
            return nodeOptions;
        }

        return $"{nodeOptions.TrimEnd()} {limits.NodeHeapOption}";
    }
}
