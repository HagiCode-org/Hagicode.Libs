using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Puts an agent CLI launch inside a memory- and CPU-limited systemd user scope. Every launch path calls this one helper after it
/// has finished its own <see cref="ProcessStartInfo" />, so every path produces identical limits.
/// </summary>
/// <remarks>
/// <c>systemd-run --scope</c> registers the scope and then replaces itself with the CLI, so the started process id is the
/// CLI's id and the standard streams and exit code pass through unchanged.
/// Memory and CPU are decided separately: a limit the host can enforce still applies when the other cannot be enforced.
/// </remarks>
public static class CgroupScopeLauncher
{
    internal const string NodeOptionsVariableName = "NODE_OPTIONS";
    private const string NodeHeapOptionName = "--max-old-space-size";

    /// <summary>
    /// Wraps <paramref name="startInfo" /> when <paramref name="limitProvider" /> reports a limit and the shared
    /// <see cref="CgroupScopeCapabilityProbe.Default" /> says the host can enforce at least one part of it.
    /// </summary>
    /// <param name="startInfo">The finished start information. It is changed in place.</param>
    /// <param name="limitProvider">The limit source, or <see langword="null" /> for no limit.</param>
    /// <param name="logger">Receives one warning per unsupported resource and reason.</param>
    /// <returns><see langword="true" /> when the start information was wrapped.</returns>
    public static bool Apply(ProcessStartInfo startInfo, ICliResourceLimitProvider? limitProvider, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return Apply(startInfo, limitProvider?.GetCurrent(), CgroupScopeCapabilityProbe.Default, logger);
    }

    /// <summary>
    /// Wraps <paramref name="startInfo" /> behind <c>systemd-run --user --scope --quiet -p ... --</c>, with the memory properties
    /// (and the Node.js heap option in <c>NODE_OPTIONS</c>) when memory is enforced and <c>CPUQuota</c> when CPU is enforced.
    /// Without a limit nothing changes. A limit the host cannot enforce is skipped (fail open), and one warning is logged per
    /// resource and reason for the lifetime of <paramref name="probe" />.
    /// </summary>
    /// <param name="startInfo">The finished start information. It is changed in place.</param>
    /// <param name="limits">The limits, or <see langword="null" /> for no limit.</param>
    /// <param name="probe">The host capability probe.</param>
    /// <param name="logger">Receives one warning per unsupported resource and reason.</param>
    /// <returns><see langword="true" /> when the start information was wrapped.</returns>
    public static bool Apply(ProcessStartInfo startInfo, CliResourceLimits? limits, CgroupScopeCapabilityProbe probe, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(probe);

        if (limits is null)
        {
            return false;
        }

        var memoryCapability = limits.MemoryMaxMegabytes is null ? null : probe.GetCapability();
        var cpuCapability = limits.CpuPercent is null ? null : probe.GetCpuCapability();
        var memoryEnforced = memoryCapability is { Supported: true, SystemdRunPath: not null };
        var cpuEnforced = cpuCapability is { Supported: true, SystemdRunPath: not null };

        if (memoryCapability is { Supported: false } unsupportedMemory)
        {
            WarnUnsupported(probe, logger, CgroupScopeResource.Memory, unsupportedMemory, $"Agent CLI memory limit of {limits.MemoryMaxMegabytes} MB");
        }

        if (cpuCapability is { Supported: false } unsupportedCpu)
        {
            WarnUnsupported(probe, logger, CgroupScopeResource.Cpu, unsupportedCpu, $"Agent CLI CPU limit of {limits.CpuPercent}%");
        }

        if (!memoryEnforced && !cpuEnforced)
        {
            return false;
        }

        // Only the Windows cmd.exe launch uses the single argument string, and that path never reaches a supported (Linux) host.
        if (!string.IsNullOrEmpty(startInfo.Arguments) || string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            return false;
        }

        var properties = new List<string>();
        if (memoryEnforced)
        {
            properties.AddRange(limits.MemorySystemdRunProperties);
        }

        if (cpuEnforced)
        {
            properties.AddRange(limits.CpuSystemdRunProperties);
        }

        var systemdRunPath = memoryEnforced ? memoryCapability!.SystemdRunPath! : cpuCapability!.SystemdRunPath!;
        var executablePath = startInfo.FileName;
        var arguments = startInfo.ArgumentList.ToArray();

        startInfo.FileName = systemdRunPath;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("--user");
        startInfo.ArgumentList.Add("--scope");
        startInfo.ArgumentList.Add("--quiet");
        foreach (var property in properties)
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(property);
        }

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(executablePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (memoryEnforced)
        {
            startInfo.Environment.TryGetValue(NodeOptionsVariableName, out var nodeOptions);
            startInfo.Environment[NodeOptionsVariableName] = MergeNodeOptions(nodeOptions, limits.NodeHeapOption!);
        }

        return true;
    }

    /// <summary>
    /// Adds the heap option to existing <c>NODE_OPTIONS</c> content. A caller's own <c>--max-old-space-size</c> is kept as it is.
    /// </summary>
    internal static string MergeNodeOptions(string? nodeOptions, string nodeHeapOption)
    {
        if (string.IsNullOrWhiteSpace(nodeOptions))
        {
            return nodeHeapOption;
        }

        // Node.js accepts the option with dashes or underscores.
        if (nodeOptions.Replace('_', '-').Contains(NodeHeapOptionName, StringComparison.Ordinal))
        {
            return nodeOptions;
        }

        return $"{nodeOptions.TrimEnd()} {nodeHeapOption}";
    }

    private static void WarnUnsupported(
        CgroupScopeCapabilityProbe probe,
        ILogger? logger,
        CgroupScopeResource resource,
        CgroupScopeCapability capability,
        string limitDescription)
    {
        if (probe.TryMarkWarned(resource, capability.Reason))
        {
            (logger ?? NullLogger.Instance).LogWarning(
                "{LimitDescription} is not enforced on this host ({Reason}); agent CLIs start without it.",
                limitDescription,
                capability.ReasonCode);
        }
    }
}
