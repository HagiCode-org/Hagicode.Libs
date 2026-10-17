namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>Category of a timed call: an operation under test, or a call made while building a fixture.</summary>
public static class TimingCategories
{
    public const string Operation = "operation";
    public const string FixtureSetup = "fixture-setup";
}

/// <summary>One timed call of an <c>IGitClient</c> operation.</summary>
/// <param name="Operation">The client member name, for example <c>GetStatusAsync</c>.</param>
/// <param name="Scenario">The named scenario, or <c>functional</c> for a test that is not a scenario.</param>
/// <param name="TestName">The full name of the test that made the call.</param>
/// <param name="Iteration">Zero-based iteration index within the scenario.</param>
/// <param name="WarmUp">True when the iteration is a discarded warm-up.</param>
/// <param name="Category">Either <see cref="TimingCategories.Operation" /> or <see cref="TimingCategories.FixtureSetup" />.</param>
/// <param name="DurationMs">Elapsed time measured with a monotonic clock.</param>
/// <param name="Success">Whether the call returned a success result.</param>
/// <param name="FailureCode">The failure code when the call failed; the exception type when it threw.</param>
public sealed record DurationRecord(
    string Operation,
    string Scenario,
    string TestName,
    int Iteration,
    bool WarmUp,
    string Category,
    double DurationMs,
    bool Success,
    string? FailureCode);
