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

    public static CgroupScopeCapabilityProbe SupportedProbe(string systemdRunPath = FakeSystemdRunPath) =>
        new(static () => true, () => systemdRunPath, static (_, _, _) => true);

    public static CgroupScopeCapabilityProbe UnsupportedProbe(CgroupScopeCapabilityReason reason) => reason switch
    {
        CgroupScopeCapabilityReason.NotLinux => new(static () => false, static () => FakeSystemdRunPath, static (_, _, _) => true),
        CgroupScopeCapabilityReason.SystemdRunMissing => new(static () => true, static () => null, static (_, _, _) => true),
        _ => new(static () => true, static () => FakeSystemdRunPath, static (_, _, _) => false)
    };

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

    public static string ReadCgroupPath(int processId)
    {
        var line = File.ReadAllLines($"/proc/{processId}/cgroup").First(static entry => entry.StartsWith("0::", StringComparison.Ordinal));
        return line[3..];
    }

    public static string ReadMemoryMax(string cgroupPath) => File.ReadAllText($"/sys/fs/cgroup{cgroupPath}/memory.max").Trim();

    public static bool CgroupExists(string cgroupPath) => Directory.Exists($"/sys/fs/cgroup{cgroupPath}");

    /// <summary>Waits until the process sits in a <c>.scope</c> cgroup, which systemd-run sets up before it runs the command.</summary>
    public static async Task<string> WaitForScopeAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var cgroupPath = ReadCgroupPath(processId);
            if (cgroupPath.EndsWith(".scope", StringComparison.Ordinal) || deadline.Elapsed > TimeSpan.FromSeconds(10))
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
