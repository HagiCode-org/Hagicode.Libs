namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>Summary of one operation within one scenario. All durations are milliseconds.</summary>
public sealed record TimingSummary(
    string Operation,
    string Scenario,
    int Count,
    double Min,
    double Mean,
    double Median,
    double P95,
    double Max,
    double Total,
    int Failures);

/// <summary>
/// Computes per-operation statistics. Warm-up records and fixture-setup records are excluded. The 95th percentile uses the
/// nearest-rank method, so small samples give deterministic values.
/// </summary>
public static class TimingStatistics
{
    public static IReadOnlyList<TimingSummary> Summarize(IEnumerable<DurationRecord> records)
    {
        return records
            .Where(record => !record.WarmUp && record.Category == TimingCategories.Operation)
            .GroupBy(record => (record.Operation, record.Scenario))
            .OrderBy(group => group.Key.Operation, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Scenario, StringComparer.Ordinal)
            .Select(group => Summarize(group.Key.Operation, group.Key.Scenario, group.ToList()))
            .ToList();
    }

    private static TimingSummary Summarize(string operation, string scenario, IReadOnlyList<DurationRecord> records)
    {
        var sorted = records.Select(record => record.DurationMs).OrderBy(value => value).ToArray();
        return new TimingSummary(
            Operation: operation,
            Scenario: scenario,
            Count: sorted.Length,
            Min: sorted[0],
            Mean: sorted.Average(),
            Median: Median(sorted),
            P95: NearestRank(sorted, 95),
            Max: sorted[^1],
            Total: sorted.Sum(),
            Failures: records.Count(record => !record.Success));
    }

    /// <summary>Nearest-rank percentile: the value at position ceil(p/100 * n), counting from 1.</summary>
    public static double NearestRank(IReadOnlyList<double> sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        rank = Math.Clamp(rank, 1, sorted.Count);
        return sorted[rank - 1];
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
