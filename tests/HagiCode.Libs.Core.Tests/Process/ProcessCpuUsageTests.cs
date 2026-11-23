using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class ProcessCpuUsageTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T0 = Start.AddMinutes(1);

    [Test]
    public void One_second_of_cpu_over_two_seconds_is_fifty_percent_of_one_processor()
    {
        var previous = Entry(cpu: TimeSpan.FromSeconds(1));
        var current = Entry(cpu: TimeSpan.FromSeconds(2));

        ProcessCpuUsage.CalculatePercentOfOneProcessor(previous, T0, current, T0.AddSeconds(2))!.Value.ShouldBe(50.0, 0.001);
    }

    [Test]
    public void The_host_percentage_divides_the_same_delta_by_the_logical_processor_count()
    {
        var previous = Entry(cpu: TimeSpan.FromSeconds(1));
        var current = Entry(cpu: TimeSpan.FromSeconds(2));

        ProcessCpuUsage.CalculatePercentOfHost(previous, T0, current, T0.AddSeconds(2), logicalProcessors: 4)!.Value.ShouldBe(12.5, 0.001);
    }

    [Test]
    public void A_replaced_process_with_the_same_id_gives_no_usage()
    {
        var previous = Entry(cpu: TimeSpan.FromSeconds(1));
        var replaced = Entry(cpu: TimeSpan.FromSeconds(2), startedAtUtc: Start.AddHours(1));

        ProcessCpuUsage.CalculatePercentOfOneProcessor(previous, T0, replaced, T0.AddSeconds(2)).ShouldBeNull();
    }

    [Test]
    public void A_missing_cpu_time_gives_no_usage()
    {
        var previous = Entry(cpu: null);
        var current = Entry(cpu: TimeSpan.FromSeconds(2));

        ProcessCpuUsage.CalculatePercentOfOneProcessor(previous, T0, current, T0.AddSeconds(2)).ShouldBeNull();
    }

    [Test]
    public void A_missing_sample_or_a_non_advancing_clock_gives_no_usage()
    {
        var previous = Entry(cpu: TimeSpan.FromSeconds(1));
        var current = Entry(cpu: TimeSpan.FromSeconds(2));

        ProcessCpuUsage.CalculatePercentOfOneProcessor(null, T0, current, T0.AddSeconds(2)).ShouldBeNull();
        ProcessCpuUsage.CalculatePercentOfOneProcessor(previous, T0, current, T0).ShouldBeNull();
    }

    private static ProcessSnapshotEntry Entry(TimeSpan? cpu, DateTimeOffset? startedAtUtc = null) => new(
        Pid: 500,
        ParentPid: 1,
        Name: "agent",
        CommandLine: null,
        StartedAtUtc: startedAtUtc ?? Start,
        CpuTime: cpu,
        MemoryBytes: 1024,
        Accessibility: ProcessAccessibility.Accessible,
        Depth: 0,
        Membership: ProcessMembership.Root);
}
