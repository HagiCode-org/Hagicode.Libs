using System.Diagnostics;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class CgroupScopeLauncherTests
{
    [Test]
    [Category("CriticalRegression")]
    public void A_4096_MB_limit_puts_the_cli_behind_systemd_run_with_every_property()
    {
        var startInfo = CreateStartInfo("/opt/agents/claude", "--print", "--output-format", "stream-json");

        var wrapped = CgroupScopeLauncher.Apply(startInfo, new CliResourceLimits(4096), ResourceLimitTestSupport.SupportedProbe());

        wrapped.ShouldBeTrue();
        startInfo.FileName.ShouldBe("/usr/bin/systemd-run");
        startInfo.ArgumentList.ShouldBe(
        [
            "--user", "--scope", "--quiet",
            "-p", "MemoryMax=4096M",
            "-p", "MemoryHigh=3481M",
            "-p", "MemorySwapMax=0",
            "-p", "TasksMax=2000",
            "--",
            "/opt/agents/claude", "--print", "--output-format", "stream-json"
        ]);
        startInfo.Arguments.ShouldBeEmpty();
    }

    [Test]
    public void Arguments_with_spaces_quotes_and_leading_dashes_stay_separate_values_after_the_separator()
    {
        string[] arguments = ["--system-prompt", "say \"hi\" to 'everyone'", "-p", "two words", "--", "--looks-like-a-flag", ""];
        var startInfo = CreateStartInfo("/opt/agents/my cli", arguments);

        CgroupScopeLauncher.Apply(startInfo, new CliResourceLimits(4096), ResourceLimitTestSupport.SupportedProbe());

        var separator = startInfo.ArgumentList.IndexOf("--");
        startInfo.ArgumentList.Skip(separator + 1).ShouldBe(["/opt/agents/my cli", .. arguments]);
        startInfo.Arguments.ShouldBeEmpty();
    }

    [Test]
    public void The_heap_flag_is_added_when_the_caller_sets_no_node_options()
    {
        var startInfo = CreateStartInfo("/opt/agents/cli");
        startInfo.Environment.Remove("NODE_OPTIONS");

        CgroupScopeLauncher.Apply(startInfo, new CliResourceLimits(4096), ResourceLimitTestSupport.SupportedProbe());

        startInfo.Environment["NODE_OPTIONS"].ShouldBe("--max-old-space-size=3072");
    }

    [Test]
    public void Existing_node_options_are_kept_and_the_heap_flag_follows_them()
    {
        var startInfo = CreateStartInfo("/opt/agents/cli");
        startInfo.Environment["NODE_OPTIONS"] = "--enable-source-maps";

        CgroupScopeLauncher.Apply(startInfo, new CliResourceLimits(4096), ResourceLimitTestSupport.SupportedProbe());

        startInfo.Environment["NODE_OPTIONS"].ShouldBe("--enable-source-maps --max-old-space-size=3072");
    }

    [TestCase("--max-old-space-size=1024")]
    [TestCase("--enable-source-maps --max_old_space_size=1024")]
    public void A_caller_heap_flag_wins_and_no_second_flag_is_added(string nodeOptions)
    {
        var startInfo = CreateStartInfo("/opt/agents/cli");
        startInfo.Environment["NODE_OPTIONS"] = nodeOptions;

        CgroupScopeLauncher.Apply(startInfo, new CliResourceLimits(4096), ResourceLimitTestSupport.SupportedProbe());

        startInfo.Environment["NODE_OPTIONS"].ShouldBe(nodeOptions);
    }

    [Test]
    public void No_limit_leaves_the_start_info_untouched_and_does_not_probe()
    {
        var trials = 0;
        var probe = new CgroupScopeCapabilityProbe(static () => true, static () => "/usr/bin/systemd-run", (_, _, _) => ++trials > 0);
        var startInfo = CreateStartInfo("/opt/agents/cli", "--print");
        startInfo.Environment.Remove("NODE_OPTIONS");

        CgroupScopeLauncher.Apply(startInfo, limits: null, probe).ShouldBeFalse();
        CgroupScopeLauncher.Apply(startInfo, limitProvider: null).ShouldBeFalse();

        startInfo.FileName.ShouldBe("/opt/agents/cli");
        startInfo.ArgumentList.ShouldBe(["--print"]);
        startInfo.Environment.ContainsKey("NODE_OPTIONS").ShouldBeFalse();
        trials.ShouldBe(0);
    }

    [TestCase(CgroupScopeCapabilityReason.NotLinux, "not-linux")]
    [TestCase(CgroupScopeCapabilityReason.SystemdRunMissing, "systemd-run-missing")]
    [TestCase(CgroupScopeCapabilityReason.UserScopeUnavailable, "user-scope-unavailable")]
    public void An_unsupported_host_starts_the_cli_unwrapped_and_warns_once_per_reason(CgroupScopeCapabilityReason reason, string reasonCode)
    {
        var probe = ResourceLimitTestSupport.UnsupportedProbe(reason);
        var logger = new CapturingLogger<CgroupScopeLauncherTests>();

        for (var launch = 0; launch < 10; launch++)
        {
            var startInfo = CreateStartInfo("/opt/agents/cli", "--print");
            startInfo.Environment["NODE_OPTIONS"] = "--enable-source-maps";

            CgroupScopeLauncher.Apply(startInfo, new CliResourceLimits(4096), probe, logger).ShouldBeFalse();

            startInfo.FileName.ShouldBe("/opt/agents/cli");
            startInfo.ArgumentList.ShouldBe(["--print"]);
            startInfo.Environment["NODE_OPTIONS"].ShouldBe("--enable-source-maps");
        }

        logger.Warnings.Count().ShouldBe(1);
        logger.Warnings.Single().ShouldContain(reasonCode);
        logger.Warnings.Single().ShouldContain("4096");
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
