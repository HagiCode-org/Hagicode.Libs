using System.Diagnostics;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>Shared helpers for the memory-limit tests.</summary>
internal static class ResourceLimitTestSupport
{
    public const string FakeSystemdRunPath = "/usr/bin/systemd-run";

    /// <summary>The CPU quota the trial scope reads back when the controller is delegated.</summary>
    public const string LimitedCpuMaxOutput = "50000 100000\n";

    /// <summary>The <c>cpu.max</c> text of a trial scope whose controller is not delegated.</summary>
    public const string UnlimitedCpuMaxOutput = "max 100000\n";

    /// <summary>Reports whether a trial call is the memory trial (<c>MemoryMax=64M</c>) rather than the CPU trial.</summary>
    public static bool IsMemoryTrial(IReadOnlyList<string> arguments) => arguments.Contains("MemoryMax=64M");

    /// <summary>A trial that succeeds: the memory trial exits 0, the CPU trial reads a limited <c>cpu.max</c>.</summary>
    public static CgroupScopeTrialResult? TrialSucceeds(IReadOnlyList<string> arguments) =>
        new(0, IsMemoryTrial(arguments) ? string.Empty : LimitedCpuMaxOutput);

    /// <summary>A probe whose memory and CPU trials both succeed.</summary>
    public static CgroupScopeCapabilityProbe SupportedProbe(string systemdRunPath = FakeSystemdRunPath) =>
        Scripted(TrialSucceeds, systemdRunPath);

    /// <summary>A probe whose trials are answered by <paramref name="trial" />, for both resources.</summary>
    public static CgroupScopeCapabilityProbe Scripted(
        Func<IReadOnlyList<string>, CgroupScopeTrialResult?> trial,
        string? systemdRunPath = FakeSystemdRunPath) =>
        new(static () => true, () => systemdRunPath, (_, arguments, _) => trial(arguments));

    public static CgroupScopeCapabilityProbe UnsupportedProbe(CgroupScopeCapabilityReason reason) => reason switch
    {
        CgroupScopeCapabilityReason.NotLinux => new(static () => false, static () => FakeSystemdRunPath, static (_, _, _) => new CgroupScopeTrialResult(0, string.Empty)),
        CgroupScopeCapabilityReason.SystemdRunMissing => new(static () => true, static () => null, static (_, _, _) => new CgroupScopeTrialResult(0, string.Empty)),
        _ => Scripted(_ => null)
    };

    /// <summary>A probe where memory is enforced but the trial scope's CPU quota is not limited.</summary>
    public static CgroupScopeCapabilityProbe CpuControllerUnavailableProbe() =>
        Scripted(arguments => IsMemoryTrial(arguments) ? new CgroupScopeTrialResult(0, string.Empty) : new CgroupScopeTrialResult(0, UnlimitedCpuMaxOutput));

    /// <summary>A probe where the memory trial fails and the CPU trial succeeds.</summary>
    public static CgroupScopeCapabilityProbe MemoryUnavailableProbe() =>
        Scripted(arguments => IsMemoryTrial(arguments) ? null : new CgroupScopeTrialResult(0, LimitedCpuMaxOutput));

    public static CliProcessManager CreateManager(ICliResourceLimitProvider? limitProvider, CgroupScopeCapabilityProbe probe, ILogger<CliProcessManager>? logger = null) =>
        new(
            new CliExecutableResolver(),
            new CliOwnedProcessRegistry(),
            new CliProcessOwnershipOptions(),
            TimeProvider.System,
            logger ?? NullLogger<CliProcessManager>.Instance,
            limitProvider,
            probe);

    /// <summary>
    /// Writes a stand-in <c>systemd-run</c> that drops every argument up to <c>--</c> and runs the rest, so a wrapped launch
    /// can run end to end on a host without systemd.
    /// </summary>
    public static string WriteFakeSystemdRun(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "systemd-run");
        File.WriteAllText(path, "#!/bin/sh\nwhile [ \"$#\" -gt 0 ] && [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public static string? FindOnPath(string name)
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }
}

internal sealed class FixedResourceLimitProvider(int? megabytes) : ICliResourceLimitProvider
{
    public int Calls { get; private set; }

    public CliResourceLimits? GetCurrent()
    {
        Calls++;
        return megabytes is { } value ? new CliResourceLimits(value) : null;
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> Warnings => Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>Reads cgroup facts of a live process on a Linux host.</summary>
internal static class RealScope
{
    private static readonly CgroupScopeCapabilityProbe HostProbe = new();

    public static CgroupScopeCapabilityProbe Probe => HostProbe;

    /// <summary>Reports the test as Skipped unless this host can run a systemd user scope.</summary>
    public static CgroupScopeCapability RequireUserScope()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Linux only: systemd user scopes do not exist on this host.");
        }

        var capability = HostProbe.GetCapability();
        if (!capability.Supported)
        {
            Assert.Ignore($"systemd-run --user --scope is unavailable on this host ({capability.ReasonCode}).");
        }

        return capability;
    }

    /// <summary>Reports the test as Skipped unless the <c>cpu</c> controller is delegated to the user manager.</summary>
    public static void RequireCpuController()
    {
        var capability = HostProbe.GetCpuCapability();
        if (!capability.Supported)
        {
            Assert.Ignore($"The cpu controller is not delegated to the systemd user manager ({capability.ReasonCode}).");
        }
    }

    public static string ReadCgroupPath(int processId)
    {
        var line = File.ReadAllLines($"/proc/{processId}/cgroup").First(static entry => entry.StartsWith("0::", StringComparison.Ordinal));
        return line[3..];
    }

    public static string ReadMemoryMax(string cgroupPath) => File.ReadAllText($"/sys/fs/cgroup{cgroupPath}/memory.max").Trim();

    public static string ReadCpuMax(string cgroupPath) => File.ReadAllText($"/sys/fs/cgroup{cgroupPath}/cpu.max").Trim();

    public static bool CgroupExists(string cgroupPath) => Directory.Exists($"/sys/fs/cgroup{cgroupPath}");

    /// <summary>
    /// Waits until the process has exec'd the command (its name is no longer <c>systemd-run</c>) and sits in a <c>.scope</c> cgroup.
    /// Before the exec the process still belongs to the caller's cgroup, which may itself be a <c>.scope</c>.
    /// </summary>
    public static async Task<string> WaitForScopeAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var comm = File.ReadAllText($"/proc/{processId}/comm").Trim();
            var cgroupPath = ReadCgroupPath(processId);
            if ((comm != "systemd-run" && cgroupPath.EndsWith(".scope", StringComparison.Ordinal)) || deadline.Elapsed > TimeSpan.FromSeconds(10))
            {
                return cgroupPath;
            }

            await Task.Delay(20);
        }
    }

    public static async Task<string> WaitForCommAsync(int processId, string expected)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var comm = File.ReadAllText($"/proc/{processId}/comm").Trim();
            if (comm == expected || deadline.Elapsed > TimeSpan.FromSeconds(10))
            {
                return comm;
            }

            await Task.Delay(20);
        }
    }

    public static async Task<bool> WaitForCgroupGoneAsync(string cgroupPath)
    {
        var deadline = Stopwatch.StartNew();
        while (CgroupExists(cgroupPath))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }
}
