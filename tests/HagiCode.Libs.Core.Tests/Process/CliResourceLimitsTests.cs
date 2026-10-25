using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CliResourceLimitsTests
{
    [Test]
    [Category("CriticalRegression")]
    public void A_4096_MB_limit_derives_the_soft_limit_heap_swap_and_tasks()
    {
        var limits = new CliResourceLimits(4096);

        limits.MemoryHighMegabytes.ShouldBe(3481);
        limits.NodeHeapMegabytes.ShouldBe(3072);
        limits.NodeHeapOption.ShouldBe("--max-old-space-size=3072");
        limits.SystemdRunProperties.ShouldBe(["MemoryMax=4096M", "MemoryHigh=3481M", "MemorySwapMax=0", "TasksMax=2000"]);
        limits.SystemdRunArguments.ShouldBe(
        [
            "--user", "--scope", "--quiet",
            "-p", "MemoryMax=4096M",
            "-p", "MemoryHigh=3481M",
            "-p", "MemorySwapMax=0",
            "-p", "TasksMax=2000",
            "--"
        ]);
    }

    [TestCase(1000, 850, 750)]
    [TestCase(1001, 850, 750)]
    [TestCase(6144, 5222, 4608)]
    public void Derived_values_round_down_to_whole_megabytes(int limit, int expectedHigh, int expectedHeap)
    {
        var limits = new CliResourceLimits(limit);

        limits.MemoryHighMegabytes.ShouldBe(expectedHigh);
        limits.NodeHeapMegabytes.ShouldBe(expectedHeap);
    }

    [TestCase("4G", 4096)]
    [TestCase("4g", 4096)]
    [TestCase("4GB", 4096)]
    [TestCase("4gb", 4096)]
    [TestCase("4096", 4096)]
    [TestCase("4096M", 4096)]
    [TestCase("4096mb", 4096)]
    [TestCase(" 512 MB ", 512)]
    public void Valid_sizes_parse_to_megabytes(string value, int expectedMegabytes)
    {
        CliResourceLimits.TryParseSize(value, out var limits).ShouldBeTrue();

        limits.ShouldNotBeNull();
        limits.MemoryMaxMegabytes.ShouldBe(expectedMegabytes);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("0G")]
    public void Empty_zero_and_negative_values_mean_no_limit_without_a_parse_failure(string? value)
    {
        CliResourceLimits.TryParseSize(value, out var limits).ShouldBeTrue();

        limits.ShouldBeNull();
    }

    [TestCase("lots")]
    [TestCase("off")]
    [TestCase("4.5G")]
    [TestCase("4T")]
    [TestCase("G4")]
    [TestCase("99999999999G")]
    public void Malformed_values_mean_no_limit_and_report_a_parse_failure(string value)
    {
        CliResourceLimits.TryParseSize(value, out var limits).ShouldBeFalse();

        limits.ShouldBeNull();
    }

    [Test]
    public void The_environment_provider_reads_AGENT_MEM_MAX_on_each_call()
    {
        var value = "4G";
        var provider = new EnvironmentCliResourceLimitProvider(() => value);

        provider.GetCurrent()!.MemoryMaxMegabytes.ShouldBe(4096);
        value = "8192";
        provider.GetCurrent()!.MemoryMaxMegabytes.ShouldBe(8192);
        value = "0";
        provider.GetCurrent().ShouldBeNull();
    }

    [Test]
    public void The_environment_provider_warns_once_for_a_malformed_value()
    {
        var logger = new CapturingLogger<EnvironmentCliResourceLimitProvider>();
        var provider = new EnvironmentCliResourceLimitProvider(static () => "lots", logger);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            provider.GetCurrent().ShouldBeNull();
        }

        logger.Warnings.Count().ShouldBe(1);
        logger.Warnings.Single().ShouldContain("AGENT_MEM_MAX");
        logger.Warnings.Single().ShouldContain("lots");
    }

    [Test]
    public void The_environment_provider_stays_silent_for_an_unset_or_zero_value()
    {
        var logger = new CapturingLogger<EnvironmentCliResourceLimitProvider>();

        new EnvironmentCliResourceLimitProvider(static () => null, logger).GetCurrent().ShouldBeNull();
        new EnvironmentCliResourceLimitProvider(static () => "0", logger).GetCurrent().ShouldBeNull();
        new EnvironmentCliResourceLimitProvider(static () => "-1", logger).GetCurrent().ShouldBeNull();

        logger.Entries.ShouldBeEmpty();
    }
}
