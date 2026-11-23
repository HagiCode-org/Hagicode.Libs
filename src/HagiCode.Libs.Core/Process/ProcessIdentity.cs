namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Identifies one operating-system process: its process id plus its start time in UTC.
/// The operating system reuses process ids, so a process id alone never names a process.
/// </summary>
public readonly record struct ProcessIdentity(int Pid, DateTimeOffset StartedAtUtc)
{
    /// <summary>
    /// Returns a value indicating whether <paramref name="other" /> names the same process: equal process ids and start times within <paramref name="tolerance" />.
    /// </summary>
    /// <param name="other">The identity to compare with.</param>
    /// <param name="tolerance">The allowed difference between the two start times.</param>
    public bool Matches(ProcessIdentity other, TimeSpan tolerance)
    {
        return Pid == other.Pid && StartTimesMatch(StartedAtUtc, other.StartedAtUtc, tolerance);
    }

    /// <summary>
    /// Returns a value indicating whether two start times differ by no more than <paramref name="tolerance" />.
    /// </summary>
    /// <param name="left">The first start time.</param>
    /// <param name="right">The second start time.</param>
    /// <param name="tolerance">The allowed difference. Must not be negative.</param>
    public static bool StartTimesMatch(DateTimeOffset left, DateTimeOffset right, TimeSpan tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance, TimeSpan.Zero);
        return (left - right).Duration() <= tolerance;
    }
}
