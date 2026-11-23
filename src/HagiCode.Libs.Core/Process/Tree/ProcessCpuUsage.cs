namespace HagiCode.Libs.Core.Process.Tree;

/// <summary>
/// Derives CPU usage from two snapshot entries of the same process.
/// </summary>
/// <remarks>
/// The snapshotter reports cumulative CPU time only. The percentage is computed from the change in that time over the time between the two captures.
/// <see cref="CalculatePercentOfOneProcessor" /> reports percent of one logical processor, so 200 means two busy cores.
/// <see cref="CalculatePercentOfHost" /> divides that by the logical processor count, so 100 means every core is busy.
/// </remarks>
public static class ProcessCpuUsage
{
    /// <summary>
    /// Returns the CPU usage of one process between two captures, as a percentage of one logical processor.
    /// </summary>
    /// <param name="previous">The entry from the earlier capture, or <see langword="null" /> when the process was absent.</param>
    /// <param name="previousCapturedAtUtc">When the earlier capture read the process table.</param>
    /// <param name="current">The entry from the later capture, or <see langword="null" /> when the process was absent.</param>
    /// <param name="currentCapturedAtUtc">When the later capture read the process table.</param>
    /// <returns>The percentage, or <see langword="null" /> when either entry is missing, a CPU time is unavailable, the start times differ, or the captures do not advance in time.</returns>
    public static double? CalculatePercentOfOneProcessor(
        ProcessSnapshotEntry? previous,
        DateTimeOffset previousCapturedAtUtc,
        ProcessSnapshotEntry? current,
        DateTimeOffset currentCapturedAtUtc)
    {
        if (previous is null || current is null)
        {
            return null;
        }

        if (previous.Pid != current.Pid
            || previous.StartedAtUtc is not { } previousStart
            || current.StartedAtUtc is not { } currentStart
            || previousStart != currentStart)
        {
            return null;
        }

        if (previous.CpuTime is not { } previousCpu || current.CpuTime is not { } currentCpu)
        {
            return null;
        }

        var elapsed = currentCapturedAtUtc - previousCapturedAtUtc;
        var cpuDelta = currentCpu - previousCpu;
        if (elapsed <= TimeSpan.Zero || cpuDelta < TimeSpan.Zero)
        {
            return null;
        }

        return cpuDelta.TotalSeconds / elapsed.TotalSeconds * 100d;
    }

    /// <summary>
    /// Returns the CPU usage of one process between two captures, as a percentage of the whole host.
    /// </summary>
    /// <param name="previous">The entry from the earlier capture, or <see langword="null" /> when the process was absent.</param>
    /// <param name="previousCapturedAtUtc">When the earlier capture read the process table.</param>
    /// <param name="current">The entry from the later capture, or <see langword="null" /> when the process was absent.</param>
    /// <param name="currentCapturedAtUtc">When the later capture read the process table.</param>
    /// <param name="logicalProcessors">The number of logical processors on the host. Must be at least 1.</param>
    /// <returns>The percentage of the host, or <see langword="null" /> under the same conditions as <see cref="CalculatePercentOfOneProcessor" />.</returns>
    public static double? CalculatePercentOfHost(
        ProcessSnapshotEntry? previous,
        DateTimeOffset previousCapturedAtUtc,
        ProcessSnapshotEntry? current,
        DateTimeOffset currentCapturedAtUtc,
        int logicalProcessors)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalProcessors, 1);

        return CalculatePercentOfOneProcessor(previous, previousCapturedAtUtc, current, currentCapturedAtUtc) / logicalProcessors;
    }
}
