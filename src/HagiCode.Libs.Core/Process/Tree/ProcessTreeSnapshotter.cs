using HagiCode.Libs.Core.Process.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Process.Tree;

/// <summary>
/// Captures process trees on Linux, Windows and macOS.
/// </summary>
/// <remarks>
/// Each capture reads the system process table once, for every requested root together. Per-process details are read only for processes that belong to a requested tree.
/// A parent link is followed only when the child did not start before its parent, which guards against process ids that were reused.
/// </remarks>
public sealed class ProcessTreeSnapshotter : IProcessTreeSnapshotter
{
    private static readonly Lazy<ProcessTreeSnapshotter> SharedDefault = new(() => new ProcessTreeSnapshotter());

    private readonly IProcessPlatform _platform;
    private readonly TimeSpan _startTimeTolerance;
    private readonly Func<ProcessIdentity, IReadOnlyList<int>>? _containmentLookup;
    private readonly ILogger<ProcessTreeSnapshotter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessTreeSnapshotter" /> class for the running operating system.
    /// </summary>
    /// <param name="options">Provides the start-time tolerance used to confirm root identities. Defaults to one second.</param>
    /// <param name="logger">The logger used for skipped candidates.</param>
    public ProcessTreeSnapshotter(CliProcessOwnershipOptions? options = null, ILogger<ProcessTreeSnapshotter>? logger = null)
        : this(
            ProcessPlatformSelector.Default,
            (options ?? new CliProcessOwnershipOptions()).StartTimeTolerance,
            containmentLookup: null,
            logger ?? NullLogger<ProcessTreeSnapshotter>.Instance)
    {
    }

    internal ProcessTreeSnapshotter(
        IProcessPlatform platform,
        TimeSpan startTimeTolerance,
        Func<ProcessIdentity, IReadOnlyList<int>>? containmentLookup,
        ILogger<ProcessTreeSnapshotter>? logger)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        ArgumentOutOfRangeException.ThrowIfLessThan(startTimeTolerance, TimeSpan.Zero);
        _startTimeTolerance = startTimeTolerance;
        _containmentLookup = containmentLookup;
        _logger = logger ?? NullLogger<ProcessTreeSnapshotter>.Instance;
    }

    /// <summary>
    /// Gets a shared snapshotter for the running operating system with default options.
    /// </summary>
    public static ProcessTreeSnapshotter Default => SharedDefault.Value;

    /// <inheritdoc />
    public async Task<ProcessTreeSnapshot> CaptureAsync(ProcessTreeSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOptions(request.Options);
        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Options.Timeout is { } timeout)
        {
            linked.CancelAfter(timeout);
        }

        try
        {
            return await Task.Run(() => CaptureCore(request, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && request.Options.Timeout is not null)
        {
            throw new TimeoutException($"The process tree snapshot did not finish within {request.Options.Timeout}.");
        }
    }

    /// <summary>
    /// Captures the trees synchronously. Termination uses this to read a subtree before it ends the processes in it.
    /// </summary>
    internal ProcessTreeSnapshot CaptureCore(ProcessTreeSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (request.Roots.Count == 0)
        {
            return new ProcessTreeSnapshot(DateTimeOffset.UtcNow, []);
        }

        var table = new ProcessTableIndex(_platform.ReadProcessTable());
        var capturedAtUtc = DateTimeOffset.UtcNow;

        var roots = new List<ProcessTreeRootSnapshot>(request.Roots.Count);
        foreach (var root in request.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roots.Add(CaptureRoot(root, table, request.Options, cancellationToken));
        }

        return new ProcessTreeSnapshot(capturedAtUtc, roots);
    }

    private static void ValidateOptions(ProcessTreeSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxProcessesPerRoot, 1);
        if (options.Timeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        }
    }

    private ProcessTreeRootSnapshot CaptureRoot(
        ProcessIdentity root,
        ProcessTableIndex table,
        ProcessTreeSnapshotOptions options,
        CancellationToken cancellationToken)
    {
        if (!table.Contains(root.Pid))
        {
            return new ProcessTreeRootSnapshot(root, ProcessTreeRootStatus.NotFound, [], Truncated: false);
        }

        var rootDetails = _platform.ReadDetails(root.Pid, options.IncludeCommandLine);
        if (rootDetails is null)
        {
            return new ProcessTreeRootSnapshot(root, ProcessTreeRootStatus.NotFound, [], Truncated: false);
        }

        // A root whose start time cannot be read is not confirmed, so nothing under it is listed.
        if (rootDetails.StartedAtUtc is not { } rootStartedAtUtc
            || !ProcessIdentity.StartTimesMatch(rootStartedAtUtc, root.StartedAtUtc, _startTimeTolerance))
        {
            return new ProcessTreeRootSnapshot(root, ProcessTreeRootStatus.IdentityMismatch, [], Truncated: false);
        }

        var walk = new TreeWalk(this, table, options, cancellationToken);
        walk.AddRoot(root.Pid, table.ParentOf(root.Pid), rootDetails, rootStartedAtUtc);
        walk.Drain();

        if (!walk.Truncated)
        {
            foreach (var containedPid in ContainmentMembersOf(root))
            {
                if (walk.Full)
                {
                    break;
                }

                walk.AddContainmentMember(containedPid, table.ParentOf(containedPid));
                walk.Drain();
            }
        }

        return new ProcessTreeRootSnapshot(root, ProcessTreeRootStatus.Found, walk.Entries, walk.Truncated);
    }

    private IEnumerable<int> ContainmentMembersOf(ProcessIdentity root)
    {
        var members = new HashSet<int>();
        if (_containmentLookup is not null)
        {
            foreach (var pid in _containmentLookup(root))
            {
                members.Add(pid);
            }
        }

        foreach (var pid in _platform.ReadContainmentMembers(root.Pid))
        {
            members.Add(pid);
        }

        members.Remove(root.Pid);
        return members.OrderBy(pid => pid);
    }

    /// <summary>
    /// Breadth-first walk of one root's tree. The queue is drained after each seed so parents are always listed before their children.
    /// </summary>
    private sealed class TreeWalk
    {
        private readonly ProcessTreeSnapshotter _owner;
        private readonly ProcessTableIndex _table;
        private readonly ProcessTreeSnapshotOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly HashSet<int> _visited = [];
        private readonly Queue<WalkNode> _queue = new();

        public TreeWalk(ProcessTreeSnapshotter owner, ProcessTableIndex table, ProcessTreeSnapshotOptions options, CancellationToken cancellationToken)
        {
            _owner = owner;
            _table = table;
            _options = options;
            _cancellationToken = cancellationToken;
        }

        public List<ProcessSnapshotEntry> Entries { get; } = [];

        public bool Truncated { get; private set; }

        public bool Full => Entries.Count >= _options.MaxProcessesPerRoot;

        public void AddRoot(int pid, int parentPid, ProcessDetails details, DateTimeOffset startedAtUtc)
        {
            Entries.Add(BuildEntry(pid, parentPid, details, depth: 0, ProcessMembership.Root));
            _visited.Add(pid);
            _queue.Enqueue(new WalkNode(pid, 0, startedAtUtc));
        }

        public void AddContainmentMember(int pid, int parentPid)
        {
            if (_visited.Contains(pid) || Full)
            {
                return;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            var details = _owner._platform.ReadDetails(pid, _options.IncludeCommandLine);
            if (details is null)
            {
                return;
            }

            Entries.Add(BuildEntry(pid, parentPid, details, depth: 1, ProcessMembership.Containment));
            _visited.Add(pid);
            _queue.Enqueue(new WalkNode(pid, 1, details.StartedAtUtc));
        }

        public void Drain()
        {
            while (_queue.Count > 0 && !Truncated)
            {
                var node = _queue.Dequeue();
                foreach (var child in _table.ChildrenOf(node.Pid))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (_visited.Contains(child.Pid))
                    {
                        continue;
                    }

                    if (Full)
                    {
                        Truncated = true;
                        return;
                    }

                    var details = _owner._platform.ReadDetails(child.Pid, _options.IncludeCommandLine);
                    if (details is null)
                    {
                        continue;
                    }

                    // A child that started before its parent cannot be a real child: the parent id was reused.
                    if (node.StartedAtUtc is { } parentStart
                        && details.StartedAtUtc is { } childStart
                        && childStart < parentStart - _owner._startTimeTolerance)
                    {
                        _owner._logger.LogDebug("Skipping process {ChildPid} as a child of {ParentPid}: it started before its parent.", child.Pid, node.Pid);
                        continue;
                    }

                    Entries.Add(BuildEntry(child.Pid, node.Pid, details, node.Depth + 1, ProcessMembership.Descendant));
                    _visited.Add(child.Pid);
                    _queue.Enqueue(new WalkNode(child.Pid, node.Depth + 1, details.StartedAtUtc));
                }
            }
        }

        private ProcessSnapshotEntry BuildEntry(int pid, int parentPid, ProcessDetails details, int depth, ProcessMembership membership)
        {
            return new ProcessSnapshotEntry(
                pid,
                parentPid,
                _table.NameOf(pid) ?? details.Name,
                details.CommandLine,
                details.StartedAtUtc,
                details.CpuTime,
                details.MemoryBytes,
                details.Accessibility,
                depth,
                membership);
        }

        private readonly record struct WalkNode(int Pid, int Depth, DateTimeOffset? StartedAtUtc);
    }

    /// <summary>
    /// Indexes one process table read by process id and by parent id.
    /// </summary>
    private sealed class ProcessTableIndex
    {
        private readonly Dictionary<int, ProcessTableRow> _byPid = [];
        private readonly Dictionary<int, List<ProcessTableRow>> _childrenByParent = [];

        public ProcessTableIndex(IReadOnlyList<ProcessTableRow> rows)
        {
            foreach (var row in rows)
            {
                _byPid.TryAdd(row.Pid, row);
            }

            foreach (var row in _byPid.Values)
            {
                if (row.ParentPid == row.Pid)
                {
                    continue;
                }

                if (!_childrenByParent.TryGetValue(row.ParentPid, out var children))
                {
                    children = [];
                    _childrenByParent[row.ParentPid] = children;
                }

                children.Add(row);
            }

            foreach (var children in _childrenByParent.Values)
            {
                children.Sort((left, right) => left.Pid.CompareTo(right.Pid));
            }
        }

        public bool Contains(int pid) => _byPid.ContainsKey(pid);

        public int ParentOf(int pid) => _byPid.TryGetValue(pid, out var row) ? row.ParentPid : 0;

        public string? NameOf(int pid) => _byPid.TryGetValue(pid, out var row) ? row.Name : null;

        public IReadOnlyList<ProcessTableRow> ChildrenOf(int pid)
        {
            return _childrenByParent.TryGetValue(pid, out var children) ? children : [];
        }
    }
}
