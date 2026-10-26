using System.Collections.Concurrent;
using System.Diagnostics;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Why a host can or cannot run an agent CLI inside a resource-limited systemd user scope.
/// </summary>
public enum CgroupScopeCapabilityReason
{
    /// <summary>The trial scope succeeded (<c>ok</c>).</summary>
    Ok,

    /// <summary>The host is not Linux (<c>not-linux</c>).</summary>
    NotLinux,

    /// <summary><c>systemd-run</c> is not on <c>PATH</c> (<c>systemd-run-missing</c>).</summary>
    SystemdRunMissing,

    /// <summary>The trial scope failed, for example without a user manager or delegation (<c>user-scope-unavailable</c>).</summary>
    UserScopeUnavailable,

    /// <summary>
    /// The trial scope ran, but its CPU quota was not limited, so the <c>cpu</c> controller is not delegated
    /// (<c>cpu-controller-unavailable</c>). Only the CPU capability reports this reason.
    /// </summary>
    CpuControllerUnavailable
}

/// <summary>
/// The result of <see cref="CgroupScopeCapabilityProbe" />.
/// </summary>
/// <param name="Reason">Why the host can or cannot enforce a limit.</param>
/// <param name="SystemdRunPath">The absolute <c>systemd-run</c> path used for launches, when it was found.</param>
public sealed record CgroupScopeCapability(CgroupScopeCapabilityReason Reason, string? SystemdRunPath)
{
    /// <summary>
    /// Gets a value indicating whether launches can be wrapped with this resource.
    /// </summary>
    public bool Supported => Reason == CgroupScopeCapabilityReason.Ok;

    /// <summary>
    /// Gets the reason as its wire string: <c>ok</c>, <c>not-linux</c>, <c>systemd-run-missing</c>, <c>user-scope-unavailable</c>
    /// or <c>cpu-controller-unavailable</c>.
    /// </summary>
    public string ReasonCode => ToReasonCode(Reason);

    /// <summary>
    /// Converts a reason to its wire string.
    /// </summary>
    public static string ToReasonCode(CgroupScopeCapabilityReason reason) => reason switch
    {
        CgroupScopeCapabilityReason.Ok => "ok",
        CgroupScopeCapabilityReason.NotLinux => "not-linux",
        CgroupScopeCapabilityReason.SystemdRunMissing => "systemd-run-missing",
        CgroupScopeCapabilityReason.UserScopeUnavailable => "user-scope-unavailable",
        CgroupScopeCapabilityReason.CpuControllerUnavailable => "cpu-controller-unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}

/// <summary>
/// The outcome of one trial <c>systemd-run</c> call that finished before its timeout.
/// </summary>
/// <param name="ExitCode">The exit code of the trial process.</param>
/// <param name="StandardOutput">Everything the trial process wrote to its standard output.</param>
public sealed record CgroupScopeTrialResult(int ExitCode, string StandardOutput);

/// <summary>
/// The resource a scope property limits. Memory and CPU are probed, reported and warned about separately.
/// </summary>
internal enum CgroupScopeResource
{
    /// <summary>The memory limit (<c>MemoryMax</c> and its companion properties).</summary>
    Memory,

    /// <summary>The CPU limit (<c>CPUQuota</c>).</summary>
    Cpu
}

/// <summary>
/// Checks once per process whether the host can run agent CLIs inside a resource-limited systemd user scope.
/// Only a trial scope proves that the user manager and cgroup delegation work, so the probe runs one, with a timeout,
/// and never on a non-Linux host. Memory and CPU are checked independently.
/// </summary>
public sealed class CgroupScopeCapabilityProbe
{
    /// <summary>
    /// The longest the trial scope may take.
    /// </summary>
    public static readonly TimeSpan TrialTimeout = TimeSpan.FromSeconds(5);

    internal const string SystemdRunExecutableName = "systemd-run";

    private static readonly string[] MemoryTrialArguments = ["--user", "--scope", "--quiet", "-p", "MemoryMax=64M", "--", "true"];

    private static readonly string[] CpuTrialArguments =
    [
        "--user", "--scope", "--quiet", "-p", "CPUQuota=50%", "--",
        "/bin/sh", "-c", "cat \"/sys/fs/cgroup$(sed -n 's/^0:://p' /proc/self/cgroup)/cpu.max\" 2>/dev/null"
    ];

    private readonly Func<bool> _isLinux;
    private readonly Func<string?> _findSystemdRun;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CgroupScopeTrialResult?> _runTrial;
    private readonly Lazy<CgroupScopeCapability> _capability;
    private readonly Lazy<CgroupScopeCapability> _cpuCapability;
    private readonly ConcurrentDictionary<(CgroupScopeResource Resource, CgroupScopeCapabilityReason Reason), byte> _warnedReasons = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CgroupScopeCapabilityProbe" /> class that checks the real host.
    /// Prefer <see cref="Default" />, which shares one cached result per process.
    /// </summary>
    public CgroupScopeCapabilityProbe()
        : this(OperatingSystem.IsLinux, FindSystemdRunOnPath, RunTrialScope)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CgroupScopeCapabilityProbe" /> class with replaceable host checks, so tests need no systemd.
    /// </summary>
    /// <param name="isLinux">Reports whether the host is Linux.</param>
    /// <param name="findSystemdRun">Returns the absolute <c>systemd-run</c> path, or <see langword="null" /> when it is missing.</param>
    /// <param name="runTrial">
    /// Runs <c>systemd-run</c> with the given arguments and timeout. Returns the exit code and standard output when the process
    /// finished in time, or <see langword="null" /> when it did not start or timed out.
    /// </param>
    public CgroupScopeCapabilityProbe(
        Func<bool> isLinux,
        Func<string?> findSystemdRun,
        Func<string, IReadOnlyList<string>, TimeSpan, CgroupScopeTrialResult?> runTrial)
    {
        _isLinux = isLinux ?? throw new ArgumentNullException(nameof(isLinux));
        _findSystemdRun = findSystemdRun ?? throw new ArgumentNullException(nameof(findSystemdRun));
        _runTrial = runTrial ?? throw new ArgumentNullException(nameof(runTrial));
        _capability = new Lazy<CgroupScopeCapability>(ProbeMemory, LazyThreadSafetyMode.ExecutionAndPublication);
        _cpuCapability = new Lazy<CgroupScopeCapability>(ProbeCpu, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the probe shared by every launch path in this process.
    /// </summary>
    public static CgroupScopeCapabilityProbe Default { get; } = new();

    /// <summary>
    /// Gets the memory capability. The first call runs the check; later calls return the cached result.
    /// The result never carries <see cref="CgroupScopeCapabilityReason.CpuControllerUnavailable" />.
    /// </summary>
    public CgroupScopeCapability GetCapability() => _capability.Value;

    /// <summary>
    /// Gets the CPU capability. The first call runs the check; later calls return the cached result.
    /// It is supported only when the trial scope's own CPU quota reads back as limited.
    /// </summary>
    public CgroupScopeCapability GetCpuCapability() => _cpuCapability.Value;

    /// <summary>
    /// Records that a warning for <paramref name="reason" /> of <paramref name="resource" /> was logged, and reports whether this is the first time.
    /// </summary>
    internal bool TryMarkWarned(CgroupScopeResource resource, CgroupScopeCapabilityReason reason) => _warnedReasons.TryAdd((resource, reason), 0);

    private CgroupScopeCapability ProbeMemory()
    {
        if (!_isLinux())
        {
            return new CgroupScopeCapability(CgroupScopeCapabilityReason.NotLinux, null);
        }

        var systemdRunPath = _findSystemdRun();
        if (string.IsNullOrWhiteSpace(systemdRunPath))
        {
            return new CgroupScopeCapability(CgroupScopeCapabilityReason.SystemdRunMissing, null);
        }

        var result = RunTrial(systemdRunPath, MemoryTrialArguments);
        return new CgroupScopeCapability(
            result is { ExitCode: 0 } ? CgroupScopeCapabilityReason.Ok : CgroupScopeCapabilityReason.UserScopeUnavailable,
            systemdRunPath);
    }

    private CgroupScopeCapability ProbeCpu()
    {
        if (!_isLinux())
        {
            return new CgroupScopeCapability(CgroupScopeCapabilityReason.NotLinux, null);
        }

        var systemdRunPath = _findSystemdRun();
        if (string.IsNullOrWhiteSpace(systemdRunPath))
        {
            return new CgroupScopeCapability(CgroupScopeCapabilityReason.SystemdRunMissing, null);
        }

        var result = RunTrial(systemdRunPath, CpuTrialArguments);
        if (result is null or { ExitCode: not 0 })
        {
            return new CgroupScopeCapability(CgroupScopeCapabilityReason.UserScopeUnavailable, systemdRunPath);
        }

        return new CgroupScopeCapability(
            IsLimitedCpuQuota(result.StandardOutput)
                ? CgroupScopeCapabilityReason.Ok
                : CgroupScopeCapabilityReason.CpuControllerUnavailable,
            systemdRunPath);
    }

    /// <summary>
    /// Reads the <c>cpu.max</c> line of a trial scope: a limited quota starts with a number (for example <c>50000 100000</c>),
    /// while an unlimited or missing controller reads <c>max</c> or nothing.
    /// </summary>
    internal static bool IsLimitedCpuQuota(string standardOutput)
    {
        var firstField = standardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(firstField, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private CgroupScopeTrialResult? RunTrial(string systemdRunPath, IReadOnlyList<string> arguments)
    {
        try
        {
            return _runTrial(systemdRunPath, arguments, TrialTimeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? FindSystemdRunOnPath()
    {
        var path = System.Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathRooted(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, SystemdRunExecutableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static CgroupScopeTrialResult? RunTrialScope(string systemdRunPath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = systemdRunPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return null;
        }

        return standardOutput.Wait(timeout)
            ? new CgroupScopeTrialResult(process.ExitCode, standardOutput.Result)
            : null;
    }
}
