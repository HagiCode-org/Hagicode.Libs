using System.Collections.Concurrent;
using System.Diagnostics;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Why a host can or cannot run an agent CLI inside a memory-limited systemd user scope.
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
    UserScopeUnavailable
}

/// <summary>
/// The result of <see cref="CgroupScopeCapabilityProbe" />.
/// </summary>
/// <param name="Reason">Why the host can or cannot enforce a limit.</param>
/// <param name="SystemdRunPath">The absolute <c>systemd-run</c> path used for launches, when it was found.</param>
public sealed record CgroupScopeCapability(CgroupScopeCapabilityReason Reason, string? SystemdRunPath)
{
    /// <summary>
    /// Gets a value indicating whether launches can be wrapped.
    /// </summary>
    public bool Supported => Reason == CgroupScopeCapabilityReason.Ok;

    /// <summary>
    /// Gets the reason as its wire string: <c>ok</c>, <c>not-linux</c>, <c>systemd-run-missing</c> or <c>user-scope-unavailable</c>.
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
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}

/// <summary>
/// Checks once per process whether the host can run agent CLIs inside a memory-limited systemd user scope.
/// Only a trial scope proves that the user manager and cgroup delegation work, so the probe runs one, with a timeout,
/// and never on a non-Linux host.
/// </summary>
public sealed class CgroupScopeCapabilityProbe
{
    /// <summary>
    /// The longest the trial scope may take.
    /// </summary>
    public static readonly TimeSpan TrialTimeout = TimeSpan.FromSeconds(5);

    internal const string SystemdRunExecutableName = "systemd-run";

    private static readonly string[] TrialArguments = ["--user", "--scope", "--quiet", "-p", "MemoryMax=64M", "--", "true"];

    private readonly Func<bool> _isLinux;
    private readonly Func<string?> _findSystemdRun;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, bool> _runTrial;
    private readonly Lazy<CgroupScopeCapability> _capability;
    private readonly ConcurrentDictionary<CgroupScopeCapabilityReason, byte> _warnedReasons = new();

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
    /// <param name="runTrial">Runs <c>systemd-run</c> with the given arguments and timeout and reports whether it exited with code 0.</param>
    public CgroupScopeCapabilityProbe(
        Func<bool> isLinux,
        Func<string?> findSystemdRun,
        Func<string, IReadOnlyList<string>, TimeSpan, bool> runTrial)
    {
        _isLinux = isLinux ?? throw new ArgumentNullException(nameof(isLinux));
        _findSystemdRun = findSystemdRun ?? throw new ArgumentNullException(nameof(findSystemdRun));
        _runTrial = runTrial ?? throw new ArgumentNullException(nameof(runTrial));
        _capability = new Lazy<CgroupScopeCapability>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the probe shared by every launch path in this process.
    /// </summary>
    public static CgroupScopeCapabilityProbe Default { get; } = new();

    /// <summary>
    /// Gets the host capability. The first call runs the check; later calls return the cached result.
    /// </summary>
    public CgroupScopeCapability GetCapability() => _capability.Value;

    /// <summary>
    /// Records that a warning for <paramref name="reason" /> was logged, and reports whether this is the first time.
    /// </summary>
    internal bool TryMarkWarned(CgroupScopeCapabilityReason reason) => _warnedReasons.TryAdd(reason, 0);

    private CgroupScopeCapability Probe()
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

        bool succeeded;
        try
        {
            succeeded = _runTrial(systemdRunPath, TrialArguments, TrialTimeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            succeeded = false;
        }

        return new CgroupScopeCapability(
            succeeded ? CgroupScopeCapabilityReason.Ok : CgroupScopeCapabilityReason.UserScopeUnavailable,
            systemdRunPath);
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

    private static bool RunTrialScope(string systemdRunPath, IReadOnlyList<string> arguments, TimeSpan timeout)
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
            return false;
        }

        process.StandardInput.Close();
        _ = process.StandardOutput.ReadToEndAsync();
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

            return false;
        }

        return process.ExitCode == 0;
    }
}
