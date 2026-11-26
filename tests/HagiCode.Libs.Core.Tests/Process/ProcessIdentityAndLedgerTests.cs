using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Process.Platform;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class ProcessIdentityAndLedgerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    [Test]
    public void A_start_time_within_the_tolerance_matches_and_one_outside_does_not()
    {
        var identity = new ProcessIdentity(42, Start);

        identity.Matches(new ProcessIdentity(42, Start.AddMilliseconds(900)), Tolerance).ShouldBeTrue();
        identity.Matches(new ProcessIdentity(42, Start.AddMilliseconds(1500)), Tolerance).ShouldBeFalse();
        ProcessIdentity.StartTimesMatch(Start, Start.AddMilliseconds(-500), Tolerance).ShouldBeTrue();
    }

    [Test]
    public void The_ledger_filters_by_provider_session_and_both()
    {
        var liveness = new FakeLiveness();
        var ledger = new CliManagedProcessLedger(liveness);
        Add(ledger, liveness, 10, "claude-code", "session-1");
        Add(ledger, liveness, 11, "codebuddy", "session-2");
        Add(ledger, liveness, 12, "claude-code", null);

        ledger.GetAlive().Count.ShouldBe(3);
        ledger.GetAlive(new CliManagedProcessQuery { ProviderName = "claude-code" }, Tolerance).Count.ShouldBe(2);
        ledger.GetAlive(new CliManagedProcessQuery { SessionId = "session-2" }, Tolerance).Select(info => info.Pid).ShouldBe(new[] { 11 });
        ledger.GetAlive(new CliManagedProcessQuery { ProviderName = "claude-code", SessionId = "session-1" }, Tolerance)
            .Select(info => info.Pid)
            .ShouldBe(new[] { 10 });
    }

    [Test]
    public void Exited_processes_and_reused_process_ids_are_pruned_from_the_ledger()
    {
        var liveness = new FakeLiveness();
        var ledger = new CliManagedProcessLedger(liveness);
        Add(ledger, liveness, 20, "exited", "s");
        Add(ledger, liveness, 21, "reused", "s");
        Add(ledger, liveness, 22, "alive", "s");
        liveness.Live.Remove(20);
        liveness.Live[21] = Start.AddHours(1);

        var alive = ledger.GetAlive(startTimeTolerance: Tolerance);

        alive.Select(info => info.Pid).ShouldBe(new[] { 22 });
        ledger.TryGet(new ProcessIdentity(20, Start), out _).ShouldBeFalse();
        ledger.TryGet(new ProcessIdentity(21, Start), out _).ShouldBeFalse();
    }

    [Test]
    public void Removing_an_entry_reports_whether_it_was_present()
    {
        var liveness = new FakeLiveness();
        var ledger = new CliManagedProcessLedger(liveness);
        Add(ledger, liveness, 30, "p", null);

        ledger.Remove(new ProcessIdentity(30, Start)).ShouldBeTrue();
        ledger.Remove(new ProcessIdentity(30, Start)).ShouldBeFalse();
    }

    [Test]
    public void An_exited_root_with_processes_left_in_its_scope_stays_residual_but_never_alive()
    {
        var (ledger, liveness, platform) = CreateWithScope();
        Add(ledger, liveness, 40, "claude-code", "session-1");
        platform.Remove(40);
        liveness.Live.Remove(40);

        ledger.GetAlive(startTimeTolerance: Tolerance).ShouldBeEmpty();

        var residual = ledger.GetResidual(new CliManagedProcessQuery { SessionId = "session-1" });
        residual.Select(info => info.Pid).ShouldBe(new[] { 40 });
        ledger.GetResidualMembers(new ProcessIdentity(40, Start)).ShouldBe(new[] { 41, 42 });
        ledger.GetResidual(new CliManagedProcessQuery { SessionId = "other" }).ShouldBeEmpty();
    }

    [Test]
    public void A_residual_entry_disappears_once_its_scope_is_empty()
    {
        var (ledger, liveness, platform) = CreateWithScope();
        Add(ledger, liveness, 40, "claude-code", "session-1");
        platform.Remove(40);
        liveness.Live.Remove(40);
        ledger.GetResidual().Count.ShouldBe(1);

        platform.Remove(41).Remove(42);

        ledger.GetResidual().ShouldBeEmpty();
        ledger.GetResidualMembers(new ProcessIdentity(40, Start)).ShouldBeEmpty();
    }

    [Test]
    public void A_removed_entry_without_a_scope_leaves_no_residual_record()
    {
        var liveness = new FakeLiveness();
        var platform = new FakeProcessPlatform().Add(50, 1, "agent", Start);
        var ledger = new CliManagedProcessLedger(liveness, platform, Timeout.InfiniteTimeSpan);
        Add(ledger, liveness, 50, "claude-code", "session-1");

        ledger.Remove(new ProcessIdentity(50, Start)).ShouldBeTrue();

        ledger.GetResidual().ShouldBeEmpty();
    }

    [Test]
    public void The_scope_key_is_picked_up_by_a_later_query_when_the_first_read_found_none()
    {
        var liveness = new FakeLiveness();
        var platform = new FakeProcessPlatform().Add(60, 1, "agent", Start).Add(61, 1, "worker", Start);
        platform.ContainmentMembersByKey["scope-60"] = [60, 61];
        var ledger = new CliManagedProcessLedger(liveness, platform, Timeout.InfiniteTimeSpan);
        Add(ledger, liveness, 60, "claude-code", "session-1");

        platform.ContainmentKeys[60] = "scope-60";
        ledger.GetAlive(startTimeTolerance: Tolerance).Count.ShouldBe(1);
        platform.Remove(60);
        liveness.Live.Remove(60);

        ledger.GetAlive(startTimeTolerance: Tolerance).ShouldBeEmpty();
        ledger.GetResidual().Select(info => info.Pid).ShouldBe(new[] { 60 });
    }

    private static (CliManagedProcessLedger Ledger, FakeLiveness Liveness, FakeProcessPlatform Platform) CreateWithScope()
    {
        var liveness = new FakeLiveness();
        var platform = new FakeProcessPlatform()
            .Add(40, 1, "agent", Start)
            .Add(41, 1, "worker-a", Start.AddSeconds(1))
            .Add(42, 1, "worker-b", Start.AddSeconds(2));
        platform.ContainmentKeys[40] = "/user.slice/run-u40.scope";
        platform.ContainmentMembersByKey["/user.slice/run-u40.scope"] = [40, 41, 42];
        return (new CliManagedProcessLedger(liveness, platform, Timeout.InfiniteTimeSpan), liveness, platform);
    }

    private static void Add(CliManagedProcessLedger ledger, FakeLiveness liveness, int pid, string provider, string? sessionId)
    {
        var identity = new ProcessIdentity(pid, Start);
        liveness.Live[pid] = Start;
        ledger.Add(
            new CliManagedProcessInfo(identity, provider, sessionId, "/bin/agent", null, CliManagedProcessOrigin.Started, IsContained: false),
            containment: null);
    }

    private sealed class FakeLiveness : IProcessLivenessReader
    {
        public Dictionary<int, DateTimeOffset> Live { get; } = new();

        public bool TryGetStartTimeUtc(int pid, out DateTimeOffset startedAtUtc)
        {
            if (Live.TryGetValue(pid, out var found))
            {
                startedAtUtc = found;
                return true;
            }

            startedAtUtc = default;
            return false;
        }
    }
}
