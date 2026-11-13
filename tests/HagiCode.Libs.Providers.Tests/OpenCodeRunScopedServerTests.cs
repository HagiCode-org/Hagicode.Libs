using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Covers how the OpenCode standalone host keys and stops servers for runs that carry run-scoped values.
/// The key tests need no process. The lifecycle tests start real <c>opencode serve</c> processes and are skipped when
/// no <c>opencode</c> executable is on PATH.
/// </summary>
[TestFixture]
public sealed class OpenCodeRunScopedServerTests
{
    private const string SessionVariable = "HAGI_SESSION_ID";
    private const string ProjectVariable = "HAGI_PROJECT_NAME";

    [Test]
    public void BuildRuntimeKey_TwoSessions_GetDifferentKeys()
    {
        var first = KeyFor(SessionRun("session-a"));
        var second = KeyFor(SessionRun("session-b"));

        first.ShouldNotBe(second);
    }

    [Test]
    public void BuildRuntimeKey_OneSession_SharesOneKey()
    {
        KeyFor(SessionRun("session-a", project: "Demo")).ShouldBe(KeyFor(SessionRun("session-a", project: "Demo")));
    }

    [Test]
    public void BuildRuntimeKey_ProjectValueDoesNotChangeASessionsKey()
    {
        KeyFor(SessionRun("session-a", project: "Demo")).ShouldBe(KeyFor(SessionRun("session-a", project: "Other")));
    }

    [Test]
    public void BuildRuntimeKey_SessionlessRunWithoutRunValues_KeepsTheSharedKey()
    {
        var shared = OpenCodeStandaloneServerHost.BuildRuntimeKey(
            new OpenCodeStandaloneServerOptions { WorkingDirectory = "/work" },
            new Dictionary<string, string?>());
        var sessionless = KeyFor(new Dictionary<string, string?>());

        sessionless.ShouldBe(shared);
    }

    [Test]
    public void BuildRuntimeKey_SessionlessRunWithProjectValue_IsNotTheSharedKey()
    {
        KeyFor(new Dictionary<string, string?> { [ProjectVariable] = "Demo" })
            .ShouldNotBe(KeyFor(new Dictionary<string, string?>()));
    }

    [Test]
    public void SessionIdOf_IgnoresBlankAndMissingValues()
    {
        OpenCodeStandaloneServerHost.SessionIdOf(new Dictionary<string, string?> { [SessionVariable] = "  " }).ShouldBeNull();
        OpenCodeStandaloneServerHost.SessionIdOf(new Dictionary<string, string?> { [ProjectVariable] = "Demo" }).ShouldBeNull();
        OpenCodeStandaloneServerHost.SessionIdOf(new Dictionary<string, string?> { ["hagi_session_id"] = " session-a " }).ShouldBe("session-a");
    }

    [Test]
    public async Task TwoSessions_StartTwoServers_AndOneSessionSharesItsServer()
    {
        var executable = FindOpenCode();
        if (executable is null) Assert.Ignore("opencode is not on PATH.");

        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        var first = await host.AcquireAsync(ServerOptions(executable!, SessionRun("session-a")));
        var second = await host.AcquireAsync(ServerOptions(executable!, SessionRun("session-b")));
        var firstAgain = await host.AcquireAsync(ServerOptions(executable!, SessionRun("session-a")));

        second.RuntimeKey.ShouldNotBe(first.RuntimeKey);
        firstAgain.ShouldBeSameAs(first);
    }

    [Test]
    public async Task SessionlessRuns_ShareTheOriginalServer()
    {
        var executable = FindOpenCode();
        if (executable is null) Assert.Ignore("opencode is not on PATH.");

        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        var first = await host.AcquireAsync(ServerOptions(executable!, new Dictionary<string, string?>()));
        var second = await host.AcquireAsync(ServerOptions(executable!, new Dictionary<string, string?>()));

        second.ShouldBeSameAs(first);
    }

    [Test]
    public async Task IdleSessionServer_IsStopped_ButABusyOneIsKept()
    {
        var executable = FindOpenCode();
        if (executable is null) Assert.Ignore("opencode is not on PATH.");

        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        var idle = await host.AcquireAsync(ServerOptions(executable!, SessionRun("session-idle")));
        var busy = await host.AcquireAsync(ServerOptions(executable!, SessionRun("session-busy")));
        busy.BeginRun();

        var later = DateTimeOffset.UtcNow.AddHours(1);
        host.SweepIdleSessionRuntimes(later).ShouldBe(1);

        var reacquired = await host.AcquireAsync(ServerOptions(executable!, SessionRun("session-busy")));
        reacquired.ShouldBeSameAs(busy);

        busy.EndRun();
        host.SweepIdleSessionRuntimes(later.AddHours(1)).ShouldBe(1);
        idle.ShouldNotBeNull();
    }

    [Test]
    public async Task SweepLeavesTheSharedServerAlone()
    {
        var executable = FindOpenCode();
        if (executable is null) Assert.Ignore("opencode is not on PATH.");

        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        await host.AcquireAsync(ServerOptions(executable!, new Dictionary<string, string?>()));

        host.SweepIdleSessionRuntimes(DateTimeOffset.UtcNow.AddDays(1)).ShouldBe(0);
    }

    [Test]
    public void CreateStartInfo_InjectedRunValue_IsLowerThanResolvedAndConfiguredValues()
    {
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver());
        var options = new OpenCodeStandaloneServerOptions
        {
            WorkingDirectory = Path.GetTempPath(),
            RunEnvironmentVariables = new Dictionary<string, string?>
            {
                [SessionVariable] = "injected-session",
                [ProjectVariable] = "injected-project",
                ["HAGI_PROJECT_ID"] = "   ",
            },
            EnvironmentVariables = new Dictionary<string, string?> { [ProjectVariable] = "configured-project" },
        };
        var resolved = new Dictionary<string, string?> { [SessionVariable] = "process-session" };

        var startInfo = launcher.CreateStartInfo(Path.Combine(Path.GetTempPath(), "opencode"), options, resolved, 4096);

        startInfo.Environment[SessionVariable].ShouldBe("process-session");
        startInfo.Environment[ProjectVariable].ShouldBe("configured-project");
        startInfo.Environment.ContainsKey("HAGI_PROJECT_ID").ShouldBeFalse();
    }

    private static Dictionary<string, string?> SessionRun(string sessionId, string? project = null)
    {
        var run = new Dictionary<string, string?> { [SessionVariable] = sessionId };
        if (project is not null)
        {
            run[ProjectVariable] = project;
        }

        return run;
    }

    private static string KeyFor(IReadOnlyDictionary<string, string?> run)
    {
        return OpenCodeStandaloneServerHost.BuildRuntimeKey(
            new OpenCodeStandaloneServerOptions { WorkingDirectory = "/work", RunEnvironmentVariables = run },
            new Dictionary<string, string?>());
    }

    private static OpenCodeStandaloneServerOptions ServerOptions(string executable, IReadOnlyDictionary<string, string?> run)
    {
        return new OpenCodeStandaloneServerOptions
        {
            ExecutablePath = executable,
            WorkingDirectory = Path.GetTempPath(),
            RunEnvironmentVariables = run,
            SessionIdleTimeout = TimeSpan.FromMinutes(10),
            StartupTimeout = TimeSpan.FromSeconds(60),
        };
    }

    private static string? FindOpenCode()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "opencode");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
