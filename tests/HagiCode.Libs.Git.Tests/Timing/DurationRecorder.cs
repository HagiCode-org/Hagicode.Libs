using System.Collections.Concurrent;

namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>
/// Collects <see cref="DurationRecord" /> values from every timed client call in the test run. <see cref="Shared" /> is the
/// process-wide recorder that the test run writes out at the end.
/// </summary>
public sealed class DurationRecorder
{
    private readonly ConcurrentQueue<DurationRecord> _records = new();

    /// <summary>The process-wide recorder.</summary>
    public static DurationRecorder Shared { get; } = new();

    /// <summary>Adds one record.</summary>
    public void Add(DurationRecord record) => _records.Enqueue(record);

    /// <summary>Returns the records added so far, in arrival order.</summary>
    public IReadOnlyList<DurationRecord> Snapshot() => _records.ToArray();
}
