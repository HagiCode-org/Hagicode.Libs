namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>The scenario, iteration and category that the next timed call belongs to. Flows through async calls.</summary>
public sealed record TimingScope(string Scenario, int Iteration, bool WarmUp, string Category);

/// <summary>Ambient timing context. Set by <see cref="ScenarioRunner" /> and <see cref="FixtureSetupScope" />.</summary>
public static class TimingContext
{
    private static readonly AsyncLocal<TimingScope?> Current = new();

    /// <summary>The active scope, or <see langword="null" /> for a plain functional test.</summary>
    public static TimingScope? Scope
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

/// <summary>
/// Marks calls made while a fixture is being built. They are recorded under <see cref="TimingCategories.FixtureSetup" />
/// and never mixed into the statistics of the operation under test.
/// </summary>
public sealed class FixtureSetupScope : IDisposable
{
    private readonly TimingScope? _previous;

    public FixtureSetupScope()
    {
        _previous = TimingContext.Scope;
        TimingContext.Scope = new TimingScope("fixture-setup", 0, WarmUp: false, TimingCategories.FixtureSetup);
    }

    public void Dispose()
    {
        TimingContext.Scope = _previous;
    }
}
