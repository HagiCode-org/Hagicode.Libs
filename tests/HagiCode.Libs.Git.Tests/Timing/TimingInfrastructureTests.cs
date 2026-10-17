using System.Text.Json;
using HagiCode.Libs.Git.Tests.Support;

namespace HagiCode.Libs.Git.Tests.Timing;

/// <summary>Task 3.2: the timing infrastructure itself, with a private recorder so the run's output stays clean.</summary>
[TestFixture]
public sealed class TimingInfrastructureTests : GitTestBase
{
    [Test]
    public async Task Warm_up_iterations_are_discarded_so_two_plus_five_gives_five_samples()
    {
        var recorder = new DurationRecorder();
        var client = new TimedGitClient(new GitClient(new GitClientOptions()), recorder);

        await ScenarioRunner.RunAsync(
            "warm-up-check",
            _ => client.IsRepositoryAsync(Environment.Root),
            warmUp: 2,
            measured: 5);

        var records = recorder.Snapshot();
        var summary = TimingStatistics.Summarize(records).Single(item => item.Scenario == "warm-up-check");
        Assert.That(records, Has.Count.EqualTo(7));
        Assert.That(records.Count(record => record.WarmUp), Is.EqualTo(2));
        Assert.That(summary.Count, Is.EqualTo(5));
    }

    [Test]
    public void Statistics_match_a_fixed_sample()
    {
        var records = Enumerable.Range(1, 10)
            .Select(value => Record("Op", value, success: true))
            .Append(Record("Op", 0.5, success: false))
            .ToList();

        var summary = TimingStatistics.Summarize(records).Single();

        Assert.That(summary.Count, Is.EqualTo(11));
        Assert.That(summary.Min, Is.EqualTo(0.5));
        Assert.That(summary.Max, Is.EqualTo(10));
        Assert.That(summary.Total, Is.EqualTo(55.5).Within(1e-9));
        Assert.That(summary.Median, Is.EqualTo(5));
        Assert.That(summary.P95, Is.EqualTo(10), "nearest rank: ceil(0.95 * 11) = 11th of 11 sorted values");
        Assert.That(summary.Failures, Is.EqualTo(1));
    }

    [Test]
    public void Nearest_rank_percentile_is_deterministic_on_small_samples()
    {
        double[] five = [1, 2, 3, 4, 5];

        Assert.That(TimingStatistics.NearestRank(five, 95), Is.EqualTo(5));
        Assert.That(TimingStatistics.NearestRank(five, 50), Is.EqualTo(3));
    }

    [Test]
    public void Fixture_setup_records_are_kept_out_of_the_statistics()
    {
        var recorder = new DurationRecorder();
        var client = new TimedGitClient(new GitClient(new GitClientOptions()), recorder);

        using (new FixtureSetupScope())
        {
            client.IsRepositoryAsync(Environment.Root).GetAwaiter().GetResult();
        }

        var record = recorder.Snapshot().Single();
        Assert.That(record.Category, Is.EqualTo(TimingCategories.FixtureSetup));
        Assert.That(TimingStatistics.Summarize(recorder.Snapshot()), Is.Empty);
    }

    [Test]
    [Category(TestTiers.CriticalRegression)]
    public async Task An_operation_called_without_timing_code_leaves_a_record()
    {
        var repository = NewRepositoryWithCommit();

        await Client.GetStatusAsync(repository);

        var record = DurationRecorder.Shared.Snapshot()
            .Last(item => item.Operation == nameof(IGitClient.GetStatusAsync));
        Assert.That(record.TestName, Is.EqualTo(TestContext.CurrentContext.Test.FullName));
        Assert.That(record.Success, Is.True);
        Assert.That(record.DurationMs, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Files_contain_metadata_records_and_statistics_when_a_directory_is_given()
    {
        var directory = Path.Combine(Environment.Root, "timing-out");
        var records = new[] { Record("GetStatusAsync", 1.5, success: true), Record("GetStatusAsync", 2.5, success: true) };
        var metadata = new TimingMetadata(
            OperatingSystem: "test-os",
            Platform: "linux",
            Architecture: "x64",
            Runtime: ".NET test",
            GitVersion: "git version 2.x",
            FileSystem: "ext4",
            UtcTimestamp: "2026-10-16T00:00:00Z",
            WarmUpIterations: 2,
            MeasuredIterations: 7);

        var (json, csv) = TimingWriter.Write(directory, metadata, records, TimingStatistics.Summarize(records));

        Assert.That(Path.GetFileName(json), Is.EqualTo("git-timing-linux-x64.json"));
        Assert.That(Path.GetFileName(csv), Is.EqualTo("git-timing-linux-x64.csv"));
        using var document = JsonDocument.Parse(File.ReadAllText(json));
        Assert.That(document.RootElement.GetProperty("metadata").GetProperty("gitVersion").GetString(), Is.EqualTo("git version 2.x"));
        Assert.That(document.RootElement.GetProperty("records").GetArrayLength(), Is.EqualTo(2));
        Assert.That(document.RootElement.GetProperty("statistics").GetArrayLength(), Is.EqualTo(1));
        var lines = File.ReadAllLines(csv);
        Assert.That(lines[0], Does.StartWith("operation,scenario,test,iteration"));
        Assert.That(lines, Has.Length.EqualTo(3), "header plus one line per record");
    }

    [Test]
    public void No_output_is_configured_when_the_variable_is_unset()
    {
        var previous = System.Environment.GetEnvironmentVariable(TimingWriter.DirectoryVariable);
        try
        {
            System.Environment.SetEnvironmentVariable(TimingWriter.DirectoryVariable, null);

            Assert.That(TimingWriter.DirectoryFromEnvironment(), Is.Null);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(TimingWriter.DirectoryVariable, previous);
        }
    }

    [Test]
    public void Csv_quotes_fields_that_contain_commas_or_quotes()
    {
        var csv = TimingWriter.ToCsv([Record("Op", 1, success: false, testName: "a,\"b\"")]);

        Assert.That(csv, Does.Contain("\"a,\"\"b\"\"\""));
    }

    private static DurationRecord Record(string operation, double duration, bool success, string testName = "t")
    {
        return new DurationRecord(
            Operation: operation,
            Scenario: "functional",
            TestName: testName,
            Iteration: 0,
            WarmUp: false,
            Category: TimingCategories.Operation,
            DurationMs: duration,
            Success: success,
            FailureCode: success ? null : "CommandFailed");
    }
}
