using System.Collections.Concurrent;
using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Windows;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Keeps an in-memory record of managed CLI subprocesses, whether or not ownership persistence is enabled.
/// </summary>
/// <remarks>
/// Entries are added when a managed process starts or is registered, and removed when it stops or its handle or registration is disposed.
/// Queries also prune entries whose process has exited or whose process id now belongs to a different process, so a host that forgets to dispose a handle still gets correct results.
/// Hosts that create several <see cref="CliProcessManager" /> instances share one ledger by registering it once and passing it to each.
/// While a process is alive the ledger remembers the name of its containment (a Linux transient systemd scope). When the process is gone but other processes remain in that containment,
/// the entry moves to a residual list instead of vanishing, so <see cref="GetResidual" /> can still report them. <see cref="GetAlive" /> never returns residual entries.
/// </remarks>
public sealed class CliManagedProcessLedger
{
    private static readonly TimeSpan DefaultStartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<ProcessIdentity, LedgerEntry> _entries = new();
    private readonly ConcurrentDictionary<ProcessIdentity, string> _containmentKeys = new();
    private readonly ConcurrentDictionary<ProcessIdentity, ResidualEntry> _residual = new();
    private readonly IProcessLivenessReader _liveness;
    private readonly IProcessPlatform _platform;
    private readonly TimeSpan _keyRetryDelay;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliManagedProcessLedger" /> class that reads liveness from the running operating system.
    /// </summary>
    public CliManagedProcessLedger()
        : this(SystemProcessLivenessReader.Instance)
    {
    }

    internal CliManagedProcessLedger(IProcessLivenessReader liveness, IProcessPlatform? platform = null, TimeSpan? keyRetryDelay = null)
    {
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        _platform = platform ?? ProcessPlatformSelector.Default;
        _keyRetryDelay = keyRetryDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Returns the managed processes that are currently alive, optionally filtered by provider and session.
    /// </summary>
    /// <param name="query">The filter. <see langword="null" /> returns every alive managed process.</param>
    /// <param name="startTimeTolerance">The tolerance used to confirm that a recorded process id still belongs to the same process. Defaults to one second.</param>
    /// <returns>The alive processes. Exited processes and reused process ids are never returned.</returns>
    public IReadOnlyList<CliManagedProcessInfo> GetAlive(CliManagedProcessQuery? query = null, TimeSpan? startTimeTolerance = null)
    {
        return GetAliveEntries(query, startTimeTolerance ?? DefaultStartTimeTolerance)
            .Select(entry => entry.Info)
            .ToArray();
    }

    /// <summary>
    /// Adds or replaces the entry for a process identity.
    /// </summary>
    internal void Add(CliManagedProcessInfo info, IWindowsProcessJob? containment)
    {
        ArgumentNullException.ThrowIfNull(info);
        _entries[info.Identity] = new LedgerEntry(info, containment);
        if (!RefreshContainmentKey(info.Identity) && _keyRetryDelay != Timeout.InfiniteTimeSpan)
        {
            // A scope launcher moves the process into its scope just after it starts, so the first read can still show the launcher's cgroup.
            _ = RetryContainmentKeyAsync(info.Identity);
        }
    }

    /// <summary>
    /// Removes the entry for a process identity when present.
    /// </summary>
    internal bool Remove(ProcessIdentity identity)
    {
        if (!_entries.TryRemove(identity, out var entry))
        {
            return false;
        }

        Retire(entry);
        return true;
    }

    /// <summary>
    /// Returns the managed processes that have exited while other processes remain in their containment, optionally filtered by provider and session.
    /// </summary>
    /// <param name="query">The filter. <see langword="null" /> returns every residual entry.</param>
    /// <returns>The recorded information of each exited root. Entries whose containment has emptied are dropped.</returns>
    public IReadOnlyList<CliManagedProcessInfo> GetResidual(CliManagedProcessQuery? query = null)
    {
        // Pruning first turns an exited root that nobody has queried yet into a residual entry.
        GetAliveEntries(query: null, DefaultStartTimeTolerance);

        var residual = new List<CliManagedProcessInfo>();
        foreach (var pair in _residual)
        {
            if (ListResidualMembers(pair.Key, pair.Value).Count == 0)
            {
                _residual.TryRemove(pair);
                continue;
            }

            if (Matches(pair.Value.Info, query))
            {
                residual.Add(pair.Value.Info);
            }
        }

        return residual.OrderBy(info => info.Pid).ToArray();
    }

    /// <summary>
    /// Lists the processes that remain in the containment of an exited root, or an empty list when the identity is not residual.
    /// </summary>
    internal IReadOnlyList<int> GetResidualMembers(ProcessIdentity identity)
    {
        return _residual.TryGetValue(identity, out var entry) ? ListResidualMembers(identity, entry) : [];
    }

    private IReadOnlyList<int> ListResidualMembers(ProcessIdentity identity, ResidualEntry entry)
    {
        try
        {
            return _platform.ReadContainmentMembersByKey(entry.ContainmentKey).Where(pid => pid != identity.Pid).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads and stores the containment key of an alive entry. Returns <see langword="true" /> when the entry has a key afterwards.
    /// </summary>
    internal bool RefreshContainmentKey(ProcessIdentity identity)
    {
        if (_containmentKeys.ContainsKey(identity))
        {
            return true;
        }

        try
        {
            if (_platform.ReadContainmentKey(identity.Pid) is { } key)
            {
                _containmentKeys[identity] = key;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No readable containment: the entry stays without a key and leaves no residual record.
        }

        return false;
    }

    private async Task RetryContainmentKeyAsync(ProcessIdentity identity)
    {
        try
        {
            await Task.Delay(_keyRetryDelay).ConfigureAwait(false);
            if (_entries.ContainsKey(identity))
            {
                RefreshContainmentKey(identity);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The retry is best effort; the next query tries again.
        }
    }

    /// <summary>
    /// Called when an entry leaves the ledger: keeps a residual record when its containment still holds other processes.
    /// </summary>
    private void Retire(LedgerEntry entry)
    {
        var identity = entry.Info.Identity;
        if (!_containmentKeys.TryRemove(identity, out var key))
        {
            return;
        }

        var residual = new ResidualEntry(entry.Info, key);
        if (ListResidualMembers(identity, residual).Count > 0)
        {
            _residual[identity] = residual;
        }
    }

    /// <summary>
    /// Returns the entry for an identity when it is still recorded.
    /// </summary>
    internal bool TryGet(ProcessIdentity identity, out LedgerEntry entry)
    {
        return _entries.TryGetValue(identity, out entry!);
    }

    /// <summary>
    /// Returns the alive entries that match the query, pruning every entry that is no longer alive.
    /// </summary>
    internal IReadOnlyList<LedgerEntry> GetAliveEntries(CliManagedProcessQuery? query, TimeSpan startTimeTolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startTimeTolerance, TimeSpan.Zero);

        var alive = new List<LedgerEntry>();
        foreach (var entry in _entries.Values)
        {
            if (!IsStillSameProcess(entry.Info.Identity, startTimeTolerance))
            {
                if (_entries.TryRemove(new KeyValuePair<ProcessIdentity, LedgerEntry>(entry.Info.Identity, entry)))
                {
                    Retire(entry);
                }

                continue;
            }

            RefreshContainmentKey(entry.Info.Identity);

            if (Matches(entry.Info, query))
            {
                alive.Add(entry);
            }
        }

        return alive
            .OrderBy(entry => entry.Info.Pid)
            .ToArray();
    }

    private bool IsStillSameProcess(ProcessIdentity recorded, TimeSpan tolerance)
    {
        return _liveness.TryGetStartTimeUtc(recorded.Pid, out var actualStartedAtUtc)
               && recorded.Matches(new ProcessIdentity(recorded.Pid, actualStartedAtUtc), tolerance);
    }

    private static bool Matches(CliManagedProcessInfo info, CliManagedProcessQuery? query)
    {
        if (query is null)
        {
            return true;
        }

        if (query.ProviderName is not null && !string.Equals(info.ProviderName, query.ProviderName, StringComparison.Ordinal))
        {
            return false;
        }

        return query.SessionId is null || string.Equals(info.SessionId, query.SessionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// One recorded managed process and the Windows Job that holds it, when contained.
    /// </summary>
    internal sealed record LedgerEntry(CliManagedProcessInfo Info, IWindowsProcessJob? Containment);

    /// <summary>
    /// An exited root and the name of the containment that still holds other processes.
    /// </summary>
    private sealed record ResidualEntry(CliManagedProcessInfo Info, string ContainmentKey);
}

/// <summary>
/// Reads whether a process id names a live process and what its start time is.
/// </summary>
internal interface IProcessLivenessReader
{
    /// <summary>
    /// Returns the start time of the live process with the id, or <see langword="false" /> when no live process has it or the start time cannot be read.
    /// </summary>
    bool TryGetStartTimeUtc(int pid, out DateTimeOffset startedAtUtc);
}

/// <summary>
/// Reads liveness with <see cref="System.Diagnostics.Process" />, the same source the handle identity uses.
/// </summary>
internal sealed class SystemProcessLivenessReader : IProcessLivenessReader
{
    /// <summary>
    /// Gets the shared reader.
    /// </summary>
    public static SystemProcessLivenessReader Instance { get; } = new();

    /// <inheritdoc />
    public bool TryGetStartTimeUtc(int pid, out DateTimeOffset startedAtUtc)
    {
        startedAtUtc = default;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
