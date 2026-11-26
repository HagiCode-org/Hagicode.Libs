using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>
/// An in-memory process table for hermetic snapshot and termination tests.
/// Kill removes the process unless its id is listed in <see cref="KillLeavesRunning" />.
/// </summary>
internal sealed class FakeProcessPlatform : IProcessPlatform
{
    private readonly Dictionary<int, FakeProcessRecord> _processes = new();

    public List<(int Pid, bool IncludeCommandLine)> DetailRequests { get; } = [];

    public List<int> KillRequests { get; } = [];

    public HashSet<int> DeniedKills { get; } = [];

    public HashSet<int> KillLeavesRunning { get; } = [];

    public int TableReads { get; private set; }

    public Action<int>? AfterKill { get; set; }

    public FakeProcessPlatform Add(
        int pid,
        int parentPid,
        string name,
        DateTimeOffset startedAtUtc,
        TimeSpan? cpuTime = null,
        string? commandLine = null,
        bool startTimeUnreadable = false)
    {
        _processes[pid] = new FakeProcessRecord(
            pid,
            parentPid,
            name,
            startedAtUtc,
            cpuTime ?? TimeSpan.FromSeconds(1),
            commandLine ?? name,
            startTimeUnreadable);
        return this;
    }

    public bool IsAlive(int pid) => _processes.ContainsKey(pid);

    public IReadOnlyList<ProcessTableRow> ReadProcessTable()
    {
        TableReads++;
        return _processes.Values
            .Select(process => new ProcessTableRow(process.Pid, process.ParentPid, process.Name))
            .ToArray();
    }

    public ProcessDetails? ReadDetails(int pid, bool includeCommandLine)
    {
        DetailRequests.Add((pid, includeCommandLine));
        if (!_processes.TryGetValue(pid, out var process))
        {
            return null;
        }

        if (process.StartTimeUnreadable)
        {
            return new ProcessDetails(null, null, null, null, process.Name, ProcessAccessibility.Denied);
        }

        return new ProcessDetails(
            process.StartedAtUtc,
            process.CpuTime,
            4096,
            includeCommandLine ? process.CommandLine : null,
            process.Name,
            ProcessAccessibility.Accessible);
    }

    /// <summary>Containment key by root pid; only alive processes report their key.</summary>
    public Dictionary<int, string> ContainmentKeys { get; } = [];

    /// <summary>Member pids by containment key; members that are not alive are not listed.</summary>
    public Dictionary<string, List<int>> ContainmentMembersByKey { get; } = [];

    public IReadOnlyList<int> ReadContainmentMembers(int rootPid) => [];

    public string? ReadContainmentKey(int rootPid) =>
        _processes.ContainsKey(rootPid) && ContainmentKeys.TryGetValue(rootPid, out var key) ? key : null;

    public IReadOnlyList<int> ReadContainmentMembersByKey(string containmentKey) =>
        ContainmentMembersByKey.TryGetValue(containmentKey, out var members)
            ? members.Where(_processes.ContainsKey).ToArray()
            : [];

    public FakeProcessPlatform Remove(int pid)
    {
        _processes.Remove(pid);
        return this;
    }

    public ProcessKillResult Kill(int pid)
    {
        KillRequests.Add(pid);
        if (!_processes.ContainsKey(pid))
        {
            return new ProcessKillResult(ProcessKillStatus.AlreadyExited, "The process is gone.");
        }

        if (DeniedKills.Contains(pid))
        {
            return new ProcessKillResult(ProcessKillStatus.AccessDenied, "Access was denied.");
        }

        if (!KillLeavesRunning.Contains(pid))
        {
            _processes.Remove(pid);
        }

        AfterKill?.Invoke(pid);
        return new ProcessKillResult(ProcessKillStatus.Killed);
    }

    private sealed record FakeProcessRecord(
        int Pid,
        int ParentPid,
        string Name,
        DateTimeOffset StartedAtUtc,
        TimeSpan CpuTime,
        string CommandLine,
        bool StartTimeUnreadable);
}
