using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// Reads and ends processes on one operating system. Snapshots and identity-checked termination are built on this seam, so their logic can be tested with a scripted implementation on any host.
/// </summary>
internal interface IProcessPlatform
{
    /// <summary>
    /// Reads the process table once. Returns one row per process that could be enumerated.
    /// </summary>
    IReadOnlyList<ProcessTableRow> ReadProcessTable();

    /// <summary>
    /// Reads the details of one process.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <param name="includeCommandLine">Whether the command line is read.</param>
    /// <returns>
    /// <see langword="null" /> when no live process has the id, including a process that has exited but not been reaped.
    /// Details with <see cref="ProcessAccessibility.Denied" /> when the process exists but its start time cannot be read.
    /// </returns>
    ProcessDetails? ReadDetails(int pid, bool includeCommandLine);

    /// <summary>
    /// Lists processes that the operating system places in the containment object of <paramref name="rootPid" /> without a parent link, such as a Linux transient systemd scope.
    /// </summary>
    /// <param name="rootPid">The root process id.</param>
    /// <returns>Process ids, or an empty list when the platform has no such containment for the root.</returns>
    IReadOnlyList<int> ReadContainmentMembers(int rootPid);

    /// <summary>
    /// Ends one process by id. The caller must have verified its identity first.
    /// </summary>
    /// <param name="pid">The process id.</param>
    ProcessKillResult Kill(int pid);
}

/// <summary>
/// One row of the process table.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="ParentPid">The parent process id.</param>
/// <param name="Name">The process name, when known.</param>
internal sealed record ProcessTableRow(int Pid, int ParentPid, string? Name);

/// <summary>
/// The details of one process as read by a platform.
/// </summary>
/// <param name="StartedAtUtc">The start time in UTC, or <see langword="null" /> when unreadable.</param>
/// <param name="CpuTime">Cumulative user plus kernel CPU time, or <see langword="null" /> when unreadable.</param>
/// <param name="MemoryBytes">Resident or working set in bytes, or <see langword="null" /> when unreadable.</param>
/// <param name="CommandLine">The command line, or <see langword="null" /> when unreadable, empty or not requested.</param>
/// <param name="Name">The process name, when known.</param>
/// <param name="Accessibility">How far the details could be read.</param>
internal sealed record ProcessDetails(
    DateTimeOffset? StartedAtUtc,
    TimeSpan? CpuTime,
    long? MemoryBytes,
    string? CommandLine,
    string? Name,
    ProcessAccessibility Accessibility);

/// <summary>
/// The status of one kill request.
/// </summary>
internal enum ProcessKillStatus
{
    /// <summary>
    /// The kill signal or call was accepted.
    /// </summary>
    Killed,

    /// <summary>
    /// The process no longer exists.
    /// </summary>
    AlreadyExited,

    /// <summary>
    /// The operating system denied the right to end the process.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// The kill failed for another reason.
    /// </summary>
    Failed,
}

/// <summary>
/// The result of one kill request.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Message">A short explanation, when the status is not <see cref="ProcessKillStatus.Killed" />.</param>
internal sealed record ProcessKillResult(ProcessKillStatus Status, string? Message = null);
