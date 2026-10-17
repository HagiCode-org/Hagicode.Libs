namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>
/// Runs a named scenario with fixed warm-up and measured iteration counts. Warm-up iterations are recorded but flagged, and
/// <see cref="TimingStatistics" /> excludes them.
/// </summary>
public static class ScenarioRunner
{
    /// <summary>Warm-up iterations for dedicated scenarios (design D7).</summary>
    public const int WarmUpIterations = 2;

    /// <summary>Measured iterations for dedicated scenarios (design D7).</summary>
    public const int MeasuredIterations = 7;

    /// <summary>Runs <paramref name="body" /> for each iteration, with the scenario context set for the timed calls it makes.</summary>
    public static async Task RunAsync(string scenario, Func<int, Task> body, int warmUp = WarmUpIterations, int measured = MeasuredIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentNullException.ThrowIfNull(body);

        for (var iteration = 0; iteration < warmUp + measured; iteration++)
        {
            var previous = TimingContext.Scope;
            TimingContext.Scope = new TimingScope(scenario, iteration, WarmUp: iteration < warmUp, TimingCategories.Operation);
            try
            {
                await body(iteration).ConfigureAwait(false);
            }
            finally
            {
                TimingContext.Scope = previous;
            }
        }
    }
}
