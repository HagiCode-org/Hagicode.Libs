namespace HagiCode.Libs.Core.Process.Tree;

/// <summary>
/// Describes how far the operating system let the snapshot read a process.
/// </summary>
public enum ProcessAccessibility
{
    /// <summary>
    /// Every field of the entry was read.
    /// </summary>
    Accessible,

    /// <summary>
    /// The process was enumerated and some details were withheld, such as the command line.
    /// </summary>
    Partial,

    /// <summary>
    /// The process was enumerated but its start time could not be read, so its identity is unknown. The entry carries no start time.
    /// </summary>
    Denied,
}

/// <summary>
/// Describes how an entry was found in the tree.
/// </summary>
public enum ProcessMembership
{
    /// <summary>
    /// The requested root process itself.
    /// </summary>
    Root,

    /// <summary>
    /// Found by following parent process ids from the root.
    /// </summary>
    Descendant,

    /// <summary>
    /// Found only through containment: a Linux transient systemd scope or a Windows Job Object. Its parent chain may be broken.
    /// </summary>
    Containment,
}

/// <summary>
/// Describes the outcome of one requested root.
/// </summary>
public enum ProcessTreeRootStatus
{
    /// <summary>
    /// The root is alive and its start time matches the requested identity.
    /// </summary>
    Found,

    /// <summary>
    /// No live process has the requested id.
    /// </summary>
    NotFound,

    /// <summary>
    /// The root's id is alive but its start time does not match, or its start time cannot be read so the identity cannot be confirmed. Nothing under it is listed.
    /// </summary>
    IdentityMismatch,

    /// <summary>
    /// The root has exited, but processes it left behind are still in its containment. Only those processes are listed, as <see cref="ProcessMembership.Containment" /> members and their descendants; the root itself is not.
    /// </summary>
    Exited,
}

/// <summary>
/// Describes one process in a snapshot.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="ParentPid">The parent process id as the operating system reported it at capture time.</param>
/// <param name="Name">The process name, when known.</param>
/// <param name="CommandLine">The command line, or <see langword="null" /> when unreadable or not requested. It can contain secrets; see the README.</param>
/// <param name="StartedAtUtc">The start time in UTC, or <see langword="null" /> when the accessibility is <see cref="ProcessAccessibility.Denied" />.</param>
/// <param name="CpuTime">Cumulative user plus kernel CPU time, or <see langword="null" /> when unreadable.</param>
/// <param name="MemoryBytes">Resident set size on Linux and macOS, or working set on Windows, in bytes. <see langword="null" /> when unreadable.</param>
/// <param name="Accessibility">How far the operating system let the snapshot read this process.</param>
/// <param name="Depth">The number of parent links below the root. Containment members are reported at depth 1.</param>
/// <param name="Membership">How the entry was found.</param>
public sealed record ProcessSnapshotEntry(
    int Pid,
    int ParentPid,
    string? Name,
    string? CommandLine,
    DateTimeOffset? StartedAtUtc,
    TimeSpan? CpuTime,
    long? MemoryBytes,
    ProcessAccessibility Accessibility,
    int Depth,
    ProcessMembership Membership);

/// <summary>
/// Describes the snapshot of the tree under one requested root.
/// </summary>
/// <param name="Root">The requested root identity.</param>
/// <param name="Status">Whether the root was found, is gone, or has a different start time.</param>
/// <param name="Processes">The root first, then members in parent-before-child order. Empty unless the status is <see cref="ProcessTreeRootStatus.Found" /> or <see cref="ProcessTreeRootStatus.Exited" />.</param>
/// <param name="Truncated">Whether <see cref="ProcessTreeSnapshotOptions.MaxProcessesPerRoot" /> stopped the walk before every member was listed.</param>
public sealed record ProcessTreeRootSnapshot(
    ProcessIdentity Root,
    ProcessTreeRootStatus Status,
    IReadOnlyList<ProcessSnapshotEntry> Processes,
    bool Truncated);

/// <summary>
/// Describes one capture of the process trees under one or more roots.
/// </summary>
/// <param name="CapturedAtUtc">When the system process table was read.</param>
/// <param name="Roots">One result per requested root, in request order.</param>
public sealed record ProcessTreeSnapshot(DateTimeOffset CapturedAtUtc, IReadOnlyList<ProcessTreeRootSnapshot> Roots);

/// <summary>
/// Bounds and options for one snapshot capture.
/// </summary>
public sealed record ProcessTreeSnapshotOptions
{
    /// <summary>
    /// Gets a value indicating whether command lines are read. When <see langword="false" />, no command line is read at all.
    /// </summary>
    public bool IncludeCommandLine { get; init; } = true;

    /// <summary>
    /// Gets the maximum number of entries listed per root, including the root. Must be at least 1.
    /// </summary>
    public int MaxProcessesPerRoot { get; init; } = 512;

    /// <summary>
    /// Gets the maximum time the capture may take. When it is exceeded, the capture throws <see cref="TimeoutException" />. <see langword="null" /> means no limit.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// Describes the roots to capture and how to capture them.
/// </summary>
public sealed record ProcessTreeSnapshotRequest
{
    /// <summary>
    /// Gets the root identities to capture. Each must be a process the caller recorded earlier.
    /// </summary>
    public required IReadOnlyList<ProcessIdentity> Roots { get; init; }

    /// <summary>
    /// Gets the capture options.
    /// </summary>
    public ProcessTreeSnapshotOptions Options { get; init; } = new();
}

/// <summary>
/// Captures process trees.
/// </summary>
public interface IProcessTreeSnapshotter
{
    /// <summary>
    /// Captures the trees under the requested roots, reading the system process table once.
    /// </summary>
    /// <param name="request">The roots and options.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <returns>The captured snapshot.</returns>
    Task<ProcessTreeSnapshot> CaptureAsync(ProcessTreeSnapshotRequest request, CancellationToken cancellationToken = default);
}
