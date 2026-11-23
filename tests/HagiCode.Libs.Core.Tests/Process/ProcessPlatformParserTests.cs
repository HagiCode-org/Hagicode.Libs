using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class ProcessPlatformParserTests
{
    private const long BootTimeSeconds = 1_700_000_000;
    private string _procRoot = null!;
    private string _cgroupRoot = null!;

    [SetUp]
    public void SetUp()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-proc-{Guid.NewGuid():N}");
        _procRoot = Path.Combine(root, "proc");
        _cgroupRoot = Path.Combine(root, "cgroup");
        Directory.CreateDirectory(_procRoot);
        Directory.CreateDirectory(_cgroupRoot);
        File.WriteAllText(Path.Combine(_procRoot, "stat"), $"cpu  1 2 3\nbtime {BootTimeSeconds}\n");
    }

    [TearDown]
    public void TearDown()
    {
        var root = Path.GetDirectoryName(_procRoot)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void The_stat_parser_finds_the_name_between_the_first_open_and_the_last_close_parenthesis()
    {
        const string text = "1234 (my (weird) name) S 1 1234 1234 0 -1 4194560 100 0 0 0 10 5 0 0 20 0 1 0 98765 12345678 2000 0 0";

        LinuxProcStatParser.TryParse(text, out var stat).ShouldBeTrue();

        stat.Pid.ShouldBe(1234);
        stat.Name.ShouldBe("my (weird) name");
        stat.State.ShouldBe('S');
        stat.ParentPid.ShouldBe(1);
        stat.UserTicks.ShouldBe(10);
        stat.SystemTicks.ShouldBe(5);
        stat.StartTicks.ShouldBe(98765);
        stat.RssPages.ShouldBe(2000);
    }

    [Test]
    public void The_stat_parser_rejects_text_without_the_expected_fields()
    {
        LinuxProcStatParser.TryParse("not a stat line", out _).ShouldBeFalse();
    }

    [Test]
    public void The_unified_cgroup_path_is_read_from_the_zero_line_only()
    {
        LinuxCgroupMembership.ParseUnifiedPath("12:cpu:/legacy\n0::/user.slice/run-r1.scope\n").ShouldBe("/user.slice/run-r1.scope");
        LinuxCgroupMembership.ParseUnifiedPath("1:name=systemd:/legacy\n").ShouldBeNull();
    }

    [Test]
    public void Containment_may_expand_only_into_a_run_scope_different_from_the_own_scope()
    {
        LinuxCgroupMembership.MayExpand("/user.slice/run-r1.scope", "/user.slice/session-2.scope").ShouldBeTrue();
        LinuxCgroupMembership.MayExpand("/user.slice/session-2.scope", "/user.slice/session-2.scope").ShouldBeFalse();
        LinuxCgroupMembership.MayExpand("/user.slice/session-2.scope", "/user.slice/other.scope").ShouldBeFalse();
        LinuxCgroupMembership.MayExpand(null, "/user.slice/session-2.scope").ShouldBeFalse();
    }

    [Test]
    public void A_service_slice_or_the_root_cgroup_never_expands_containment()
    {
        LinuxCgroupMembership.MayExpand("/system.slice/nginx.service", "/user.slice/session-2.scope").ShouldBeFalse();
        LinuxCgroupMembership.MayExpand("/", "/user.slice/session-2.scope").ShouldBeFalse();
    }

    [Test]
    public void A_transient_scope_root_lists_every_member_of_its_cgroup()
    {
        WriteCgroupOfPid(100, "0::/user.slice/run-r1.scope\n");
        WriteOwnCgroup("0::/user.slice/session-2.scope\n");
        WriteCgroupProcs("/user.slice/run-r1.scope", "100\n300\n");

        new LinuxProcessPlatform(_procRoot, _cgroupRoot).ReadContainmentMembers(100).ToArray().ShouldBe(new[] { 100, 300 });
    }

    [Test]
    public void A_session_scope_service_cgroup_or_root_cgroup_is_never_expanded_even_when_its_procs_file_lists_extra_pids()
    {
        WriteOwnCgroup("0::/user.slice/session-2.scope\n");

        WriteCgroupOfPid(300, "0::/user.slice/session-2.scope\n");
        WriteCgroupProcs("/user.slice/session-2.scope", "300\n301\n");

        WriteCgroupOfPid(200, "0::/system.slice/nginx.service\n");
        WriteCgroupProcs("/system.slice/nginx.service", "200\n201\n");

        WriteCgroupOfPid(400, "0::/\n");
        WriteCgroupProcs("/", "400\n401\n");

        var platform = new LinuxProcessPlatform(_procRoot, _cgroupRoot);
        platform.ReadContainmentMembers(300).ShouldBeEmpty();
        platform.ReadContainmentMembers(200).ShouldBeEmpty();
        platform.ReadContainmentMembers(400).ShouldBeEmpty();
    }

    private void WriteCgroupOfPid(int pid, string content)
    {
        var directory = Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cgroup"), content);
    }

    private void WriteOwnCgroup(string content)
    {
        var directory = Path.Combine(_procRoot, "self");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cgroup"), content);
    }

    private void WriteCgroupProcs(string cgroupPath, string content)
    {
        var directory = Path.Combine(_cgroupRoot, cgroupPath.TrimStart('/'));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cgroup.procs"), content);
    }

    [Test]
    public void The_fake_proc_table_lists_numeric_directories_including_names_with_spaces()
    {
        WriteProcess(100, "node", "S", 1, 30, 20, 98765, 2000);
        WriteProcess(101, "my (weird) name", "S", 100, 1, 1, 98766, 10);

        var rows = new LinuxProcessPlatform(_procRoot, _cgroupRoot).ReadProcessTable();

        rows.Select(row => (row.Pid, row.ParentPid, row.Name)).OrderBy(row => row.Pid).ToArray()
            .ShouldBe(new (int, int, string?)[] { (100, 1, "node"), (101, 100, "my (weird) name") });
    }

    [Test]
    public void Details_come_from_stat_with_start_time_from_boot_time_and_clock_ticks()
    {
        WriteProcess(100, "node", "S", 1, 30, 20, 98765, 2000);

        var details = new LinuxProcessPlatform(_procRoot, _cgroupRoot).ReadDetails(100, includeCommandLine: false);

        details.ShouldNotBeNull();
        details.Accessibility.ShouldBe(ProcessAccessibility.Accessible);
        details.Name.ShouldBe("node");
        details.StartedAtUtc!.Value.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(BootTimeSeconds).AddSeconds(987.65), TimeSpan.FromMilliseconds(1));
        details.CpuTime.ShouldBe(TimeSpan.FromSeconds(0.5));
        details.CommandLine.ShouldBeNull();
    }

    [Test]
    public void The_command_line_joins_its_nul_separated_arguments_with_spaces()
    {
        WriteProcess(100, "node", "S", 1, 30, 20, 98765, 2000);
        File.WriteAllBytes(Path.Combine(_procRoot, "100", "cmdline"), "node\0app.js\0--flag\0"u8.ToArray());

        var details = new LinuxProcessPlatform(_procRoot, _cgroupRoot).ReadDetails(100, includeCommandLine: true);

        details!.CommandLine.ShouldBe("node app.js --flag");
        details.Accessibility.ShouldBe(ProcessAccessibility.Accessible);
    }

    [Test]
    public void An_unreadable_command_line_marks_the_details_partial()
    {
        WriteProcess(100, "node", "S", 1, 30, 20, 98765, 2000);
        File.WriteAllBytes(Path.Combine(_procRoot, "100", "cmdline"), []);

        var details = new LinuxProcessPlatform(_procRoot, _cgroupRoot).ReadDetails(100, includeCommandLine: true);

        details!.CommandLine.ShouldBeNull();
        details.Accessibility.ShouldBe(ProcessAccessibility.Partial);
    }

    [Test]
    public void A_zombie_or_a_missing_process_reads_as_gone()
    {
        WriteProcess(100, "done", "Z", 1, 0, 0, 98765, 0);
        var platform = new LinuxProcessPlatform(_procRoot, _cgroupRoot);

        platform.ReadDetails(100, includeCommandLine: false).ShouldBeNull();
        platform.ReadDetails(999, includeCommandLine: false).ShouldBeNull();
    }

    [Test]
    public void The_start_time_of_this_process_matches_the_framework_within_one_second()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Reads the Linux /proc filesystem.");
        }

        var details = new LinuxProcessPlatform().ReadDetails(System.Environment.ProcessId, includeCommandLine: false);
        var expected = new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

        details.ShouldNotBeNull();
        ProcessIdentity.StartTimesMatch(details.StartedAtUtc!.Value, expected, TimeSpan.FromSeconds(1)).ShouldBeTrue();
    }

    [Test]
    public void A_table_line_parses_a_pid_a_parent_and_a_name_with_spaces()
    {
        var row = PsOutputParser.ParseTableLine("  202   101 Google Chrome Helper");

        row.ShouldNotBeNull();
        row.Pid.ShouldBe(202);
        row.ParentPid.ShouldBe(101);
        row.Name.ShouldBe("Google Chrome Helper");
    }

    [Test]
    public void A_malformed_table_line_is_rejected()
    {
        PsOutputParser.ParseTableLine("garbage line").ShouldBeNull();
        PsOutputParser.ParseTableLine("  abc   1 node").ShouldBeNull();
    }

    [Test]
    public void A_detail_line_parses_the_lstart_form_rss_in_kibibytes_and_cpu_time()
    {
        var detail = PsOutputParser.ParseDetailLine("  101     1 Mon Nov  2 10:00:00 2026  2048 00:01.25 node");

        detail.ShouldNotBeNull();
        detail.Pid.ShouldBe(101);
        detail.ParentPid.ShouldBe(1);
        detail.StartedAtUtc.ShouldBe(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        detail.MemoryBytes.ShouldBe(2048L * 1024);
        detail.CpuTime.ShouldBe(TimeSpan.FromSeconds(1.25));
        detail.Name.ShouldBe("node");
    }

    [Test]
    public void A_detail_line_keeps_a_name_with_parentheses()
    {
        var detail = PsOutputParser.ParseDetailLine("  303   101 Mon Nov  2 10:00:00 2026   100 1-02:03:04.50 Google Chrome Helper (Renderer)");

        detail.ShouldNotBeNull();
        detail.Name.ShouldBe("Google Chrome Helper (Renderer)");
        detail.CpuTime.ShouldBe(TimeSpan.FromDays(1) + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(4.5));
    }

    [Test]
    public void A_detail_line_with_a_bad_start_time_is_rejected()
    {
        PsOutputParser.ParseDetailLine("  101 1 not-a-date 2048 00:01.25 node").ShouldBeNull();
    }

    [Test]
    public void The_cpu_time_form_accepts_minutes_and_seconds_and_rejects_text()
    {
        PsOutputParser.TryParseCpuTime("03:04", out var minutesAndSeconds).ShouldBeTrue();
        minutesAndSeconds.ShouldBe(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(4));
        PsOutputParser.TryParseCpuTime("bad", out _).ShouldBeFalse();
    }

    private void WriteProcess(int pid, string name, string state, int parentPid, long utime, long stime, long starttime, long rss)
    {
        var directory = Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "stat"),
            $"{pid} ({name}) {state} {parentPid} 0 0 0 -1 0 0 0 0 0 {utime} {stime} 0 0 20 0 1 0 {starttime} 0 {rss}\n");
    }
}
