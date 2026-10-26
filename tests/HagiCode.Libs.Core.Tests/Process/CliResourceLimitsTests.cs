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

    [Test]
    public void A_cpu_only_limit_has_only_the_cpuquota_property()
    {
        var limits = new CliResourceLimits(null, 200);

        limits.MemoryMaxMegabytes.ShouldBeNull();
        limits.MemoryHighMegabytes.ShouldBeNull();
        limits.NodeHeapOption.ShouldBeNull();
        limits.MemorySystemdRunProperties.ShouldBeEmpty();
        limits.CpuSystemdRunProperties.ShouldBe(["CPUQuota=200%"]);
        limits.SystemdRunArguments.ShouldBe(["--user", "--scope", "--quiet", "-p", "CPUQuota=200%", "--"]);
    }

    [Test]
    public void A_combined_limit_lists_the_memory_properties_then_the_cpu_quota()
    {
        var limits = new CliResourceLimits(4096, 200);

        limits.SystemdRunProperties.ShouldBe(["MemoryMax=4096M", "MemoryHigh=3481M", "MemorySwapMax=0", "TasksMax=2000", "CPUQuota=200%"]);
        limits.NodeHeapOption.ShouldBe("--max-old-space-size=3072");
    }

    [Test]
    public void A_memory_only_limit_keeps_the_memory_properties_and_no_cpu_quota()
    {
        var limits = new CliResourceLimits(4096);

        limits.CpuPercent.ShouldBeNull();
        limits.CpuSystemdRunProperties.ShouldBeEmpty();
        limits.SystemdRunProperties.ShouldBe(["MemoryMax=4096M", "MemoryHigh=3481M", "MemorySwapMax=0", "TasksMax=2000"]);
    }

    [Test]
    public void A_limit_needs_at_least_one_part_and_positive_values()
    {
        Should.Throw<ArgumentException>(() => new CliResourceLimits(null, null));
        Should.Throw<ArgumentOutOfRangeException>(() => new CliResourceLimits(null, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new CliResourceLimits(0, null));
    }

    [TestCase("200%", 200)]
    [TestCase("150", 150)]
    [TestCase("100%", 100)]
    [TestCase(" 75 % ", 75)]
    [TestCase("20000", 10000)]
    [TestCase("10000%", 10000)]
    public void Valid_cpu_percentages_parse_and_clamp_above_the_maximum(string value, int expectedPercent)
    {
        CliResourceLimits.TryParseCpuPercent(value, out var cpuPercent).ShouldBeTrue();

        cpuPercent.ShouldBe(expectedPercent);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("0")]
    [TestCase("0%")]
    [TestCase("-5")]
    public void Empty_zero_and_negative_cpu_values_mean_no_limit_without_a_parse_failure(string? value)
    {
        CliResourceLimits.TryParseCpuPercent(value, out var cpuPercent).ShouldBeTrue();

        cpuPercent.ShouldBeNull();
    }

    [TestCase("many")]
    [TestCase("2.5")]
    [TestCase("200%%")]
    [TestCase("2 cores")]
    public void Malformed_cpu_values_report_a_parse_failure(string value)
    {
        CliResourceLimits.TryParseCpuPercent(value, out var cpuPercent).ShouldBeFalse();

        cpuPercent.ShouldBeNull();
    }

    [Test]
    public void The_environment_provider_reads_AGENT_CPU_MAX_alone()
    {
        var provider = new EnvironmentCliResourceLimitProvider(static () => null, static () => "200%");

        var limits = provider.GetCurrent();

        limits!.MemoryMaxMegabytes.ShouldBeNull();
        limits.CpuPercent.ShouldBe(200);
    }

    [Test]
    public void The_environment_provider_combines_AGENT_MEM_MAX_and_AGENT_CPU_MAX_into_one_limit()
    {
        var provider = new EnvironmentCliResourceLimitProvider(static () => "4G", static () => "200");

        var limits = provider.GetCurrent();

        limits!.MemoryMaxMegabytes.ShouldBe(4096);
        limits.CpuPercent.ShouldBe(200);
    }

    [Test]
    public void The_environment_provider_has_no_limit_when_both_variables_are_unset_or_zero()
    {
        new EnvironmentCliResourceLimitProvider(static () => null, static () => null).GetCurrent().ShouldBeNull();
        new EnvironmentCliResourceLimitProvider(static () => "0", static () => "0%").GetCurrent().ShouldBeNull();
    }

    [Test]
    public void The_environment_provider_warns_once_per_malformed_variable()
    {
        var logger = new CapturingLogger<EnvironmentCliResourceLimitProvider>();
        var provider = new EnvironmentCliResourceLimitProvider(static () => "lots", static () => "many", logger);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            provider.GetCurrent().ShouldBeNull();
        }

        logger.Warnings.Count().ShouldBe(2);
        logger.Warnings.Count(message => message.Contains("AGENT_MEM_MAX")).ShouldBe(1);
        logger.Warnings.Count(message => message.Contains("AGENT_CPU_MAX")).ShouldBe(1);
    }

    [Test]
    public void A_malformed_cpu_value_does_not_drop_a_valid_memory_limit()
    {
        var logger = new CapturingLogger<EnvironmentCliResourceLimitProvider>();
        var provider = new EnvironmentCliResourceLimitProvider(static () => "4G", static () => "many", logger);

        var limits = provider.GetCurrent();

        limits!.MemoryMaxMegabytes.ShouldBe(4096);
        limits.CpuPercent.ShouldBeNull();
        logger.Warnings.Single().ShouldContain("AGENT_CPU_MAX");
    }
}
