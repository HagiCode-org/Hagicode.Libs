using System.Diagnostics;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

/// <summary>
/// Runs real systemd user scopes. Every test is skipped unless this is a Linux host where
/// <c>systemd-run --user --scope</c> works (the user manager must be reachable, which needs <c>XDG_RUNTIME_DIR</c>).
/// </summary>
[NonParallelizable]
[Category("Integration")]
public sealed class CgroupScopeRealScopeTests
{
    private const int SmallLimitMegabytes = 128;

    /// <summary>
    /// Proves design D6 (task 2.1) with raw <c>systemd-run</c>, before anything builds on it.
    /// Outcome on 2026-10-25 (Fedora 44, kernel 7.2, cgroup v2, systemd user manager): the pid that
    /// <see cref="System.Diagnostics.Process" /> returns becomes the <c>sleep</c> process (<c>/proc/&lt;pid&gt;/comm</c> is
    /// <c>sleep</c>), its cgroup is a <c>run-p&lt;pid&gt;-i&lt;n&gt;.scope</c> unit with <c>memory.max</c> 67108864, and the scope is
    /// gone once the process is killed. systemd-run therefore replaces itself with the command, and the tracked pid is the CLI's.
    /// </summary>
    [Test]
    public async Task Raw_systemd_run_scope_keeps_the_started_process_id_as_the_command()
    {
        var capability = RealScope.RequireUserScope();
        var startInfo = new ProcessStartInfo
        {
            FileName = capability.SystemdRunPath!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "--user", "--scope", "--quiet", "-p", "MemoryMax=64M", "--", "sleep", "30" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        string cgroupPath;
        try
        {
            (await RealScope.WaitForCommAsync(process.Id, "sleep")).ShouldBe("sleep");
            cgroupPath = RealScope.ReadCgroupPath(process.Id);
            cgroupPath.ShouldEndWith(".scope");
            RealScope.ReadMemoryMax(cgroupPath).ShouldBe("67108864");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        (await RealScope.WaitForCgroupGoneAsync(cgroupPath)).ShouldBeTrue($"scope {cgroupPath} still exists after the process was killed");
    }

    [Test]
    public async Task A_wrapped_agent_passes_stdin_stdout_and_exit_code_through_inside_a_limited_scope()
    {
        RealScope.RequireUserScope();
        var manager = ResourceLimitTestSupport.CreateManager(new FixedResourceLimitProvider(SmallLimitMegabytes), RealScope.Probe);

        await using var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-c", "read line; printf 'echo:%s\\n' \"$line\"; exit 3"]
        });

        handle.ResolvedExecutablePath.ShouldNotContain("systemd-run");
        var cgroupPath = await RealScope.WaitForScopeAsync(handle.Process.Id);
        cgroupPath.ShouldEndWith(".scope");
        RealScope.ReadMemoryMax(cgroupPath).ShouldBe((SmallLimitMegabytes * 1024L * 1024L).ToString());

        await handle.StandardInput.WriteLineAsync("two words");
        await handle.StandardInput.FlushAsync();
        var line = await handle.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await handle.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        line.ShouldBe("echo:two words");
        handle.Process.ExitCode.ShouldBe(3);
    }

    /// <remarks>
    /// The agent here is <c>sleep</c> itself. A shell that backgrounds a child and exits on the graceful SIGINT orphans that
    /// child: it leaves the process tree, so the tree kill misses it, and it keeps the scope alive until it exits. That
    /// happens without the scope too; the scope only makes it visible.
    /// </remarks>
    [Test]
    public async Task Stopping_a_wrapped_agent_ends_it_and_removes_its_scope()
    {
        RealScope.RequireUserScope();
        var manager = ResourceLimitTestSupport.CreateManager(new FixedResourceLimitProvider(SmallLimitMegabytes), RealScope.Probe);

        var handle = await manager.StartAsync(new ProcessStartContext
        {
            ExecutablePath = "sleep",
            Arguments = ["30"]
        });
        var processId = handle.Process.Id;
        (await RealScope.WaitForCommAsync(processId, "sleep")).ShouldBe("sleep");
        var cgroupPath = await RealScope.WaitForScopeAsync(processId);
        cgroupPath.ShouldEndWith(".scope");
        RealScope.ReadMemoryMax(cgroupPath).ShouldBe((SmallLimitMegabytes * 1024L * 1024L).ToString());

        await manager.StopAsync(handle);

        (await RealScope.WaitForCgroupGoneAsync(cgroupPath)).ShouldBeTrue($"scope {cgroupPath} still exists after StopAsync");
    }

    /// <summary>
    /// Task 8.2: a Node.js stand-in CLI that allocates past the limit fails with a heap error inside the scope, because the
    /// heap flag (75% of the limit) stops V8 before the cgroup does, while the same run without a limit, and with a malformed
    /// <c>AGENT_MEM_MAX</c>, completes.
    /// </summary>
    /// <remarks>
    /// Node.js is the stand-in on purpose. A non-Node allocation (for example python3) in the same scope is not killed: once it
    /// passes <c>MemoryHigh</c> (85%) with swap off, the kernel throttles it, and on 2026-10-25 a 256 MB python3 allocation in a
    /// 128 MB scope was still stalled after 90 s, while the same scope without <c>MemoryHigh</c> killed it in 0.1 s.
    /// </remarks>
    [Test]
    public async Task A_wrapped_node_cli_that_allocates_past_the_limit_fails_with_a_heap_error_while_unlimited_runs_complete()
    {
        RealScope.RequireUserScope();
        var node = ResourceLimitTestSupport.FindOnPath("node");
        if (node is null)
        {
            Assert.Ignore("node is not on PATH; it is the stand-in CLI that allocates memory.");
        }

        // About 256 MB of small integers: past the 96 MB heap flag of a 128 MB limit, and harmless without a limit.
        var context = new ProcessStartContext
        {
            ExecutablePath = node!,
            Arguments = ["-e", "const a = []; for (let i = 0; i < 16; i++) { a.push(new Array(4 * 1024 * 1024).fill(i)); } console.log('allocated', a.length);"],
            EnvironmentVariables = new Dictionary<string, string?> { ["NODE_OPTIONS"] = null },
            Timeout = TimeSpan.FromSeconds(30)
        };

        var limited = await ResourceLimitTestSupport
            .CreateManager(new FixedResourceLimitProvider(SmallLimitMegabytes), RealScope.Probe)
            .ExecuteAsync(context);
        var control = await ResourceLimitTestSupport
            .CreateManager(limitProvider: null, RealScope.Probe)
            .ExecuteAsync(context);
        var malformedLogger = new CapturingLogger<EnvironmentCliResourceLimitProvider>();
        var malformedProvider = new EnvironmentCliResourceLimitProvider(static () => "lots", malformedLogger);
        var malformed = await ResourceLimitTestSupport
            .CreateManager(malformedProvider, RealScope.Probe)
            .ExecuteAsync(context);

        limited.TimedOut.ShouldBeFalse();
        limited.ExitCode.ShouldNotBe(0, $"stdout: {limited.StandardOutput} stderr: {limited.StandardError}");
        limited.StandardOutput.ShouldNotContain("allocated");
        limited.StandardError.ShouldContain("heap out of memory");

        control.ExitCode.ShouldBe(0, control.StandardError);
        control.StandardOutput.Trim().ShouldBe("allocated 16");

        malformed.ExitCode.ShouldBe(0, malformed.StandardError);
        malformed.StandardOutput.Trim().ShouldBe("allocated 16");
        malformedLogger.Warnings.Count().ShouldBe(1);
    }
}
