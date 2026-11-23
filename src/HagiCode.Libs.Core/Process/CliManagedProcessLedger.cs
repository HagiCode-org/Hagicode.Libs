using System.Collections.Concurrent;
using HagiCode.Libs.Core.Process.Windows;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Keeps an in-memory record of managed CLI subprocesses, whether or not ownership persistence is enabled.
/// </summary>
/// <remarks>
/// Entries are added when a managed process starts or is registered, and removed when it stops or its handle or registration is disposed.
/// Queries also prune entries whose process has exited or whose process id now belongs to a different process, so a host that forgets to dispose a handle still gets correct results.
/// Hosts that create several <see cref="CliProcessManager" /> instances share one ledger by registering it once and passing it to each.
/// </remarks>
public sealed class CliManagedProcessLedger
{
    private static readonly TimeSpan DefaultStartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<ProcessIdentity, LedgerEntry> _entries = new();
    private readonly IProcessLivenessReader _liveness;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliManagedProcessLedger" /> class that reads liveness from the running operating system.
    /// </summary>
    public CliManagedProcessLedger()
        : this(SystemProcessLivenessReader.Instance)
    {
    }

    internal CliManagedProcessLedger(IProcessLivenessReader liveness)
    {
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
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
    }

    /// <summary>
    /// Removes the entry for a process identity when present.
    /// </summary>
    internal bool Remove(ProcessIdentity identity)
    {
        return _entries.TryRemove(identity, out _);
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
                _entries.TryRemove(new KeyValuePair<ProcessIdentity, LedgerEntry>(entry.Info.Identity, entry));
                continue;
            }

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
