using System.Diagnostics;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Codex;
using ManagedCode.CodexSharpSDK.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Codex child processes are attributed to the caller's session in the shared process ledger, without the ledger ever
/// stopping them.
/// </summary>
[TestFixture]
public sealed class CodexProcessLedgerTests
{
    private const string OwnerSessionId = "core-session-id";

    [Test]
    public async Task Registration_records_the_process_under_the_owner_session_and_disposing_it_only_removes_the_entry()
    {
        var manager = new CliProcessManager();
        using var process = StartLongRunning();
        try
        {
            var registration = await CodexProcessLedger.TryRegisterAsync(
                manager, OwnerSessionId, process, NullLogger.Instance, CancellationToken.None);

            registration.ShouldNotBeNull();
            var entry = manager.GetAliveManagedProcesses(new CliManagedProcessQuery
            {
                ProviderName = CodexProcessLedger.ProviderName,
                SessionId = OwnerSessionId,
            }).Single();
            entry.Pid.ShouldBe(process.Id);
            entry.ProviderName.ShouldBe("codex");
            entry.SessionId.ShouldBe(OwnerSessionId);

            await registration!.DisposeAsync();

            manager.GetAliveManagedProcesses(new CliManagedProcessQuery
            {
                ProviderName = CodexProcessLedger.ProviderName,
                SessionId = OwnerSessionId,
            }).ShouldBeEmpty();
            process.HasExited.ShouldBeFalse();
        }
        finally
        {
            StopQuietly(process);
        }
    }

    [Test]
    public async Task Without_an_owner_session_id_nothing_is_registered()
    {
        var manager = new CliProcessManager();
        using var process = StartLongRunning();
        try
        {
            var registration = await CodexProcessLedger.TryRegisterAsync(
                manager, null, process, NullLogger.Instance, CancellationToken.None);

            registration.ShouldBeNull();
            manager.GetAliveManagedProcesses().ShouldBeEmpty();
        }
        finally
        {
            StopQuietly(process);
        }
    }

    [Test]
    public async Task A_blank_owner_session_id_is_treated_as_unset()
    {
        var manager = new CliProcessManager();
        using var process = StartLongRunning();
        try
        {
            var registration = await CodexProcessLedger.TryRegisterAsync(
                manager, "   ", process, NullLogger.Instance, CancellationToken.None);

            registration.ShouldBeNull();
            manager.GetAliveManagedProcesses().ShouldBeEmpty();
        }
        finally
        {
            StopQuietly(process);
        }
    }

    [Test]
    public async Task Without_a_process_manager_nothing_is_registered()
    {
        using var process = StartLongRunning();
        try
        {
            var registration = await CodexProcessLedger.TryRegisterAsync(
                null, OwnerSessionId, process, NullLogger.Instance, CancellationToken.None);

            registration.ShouldBeNull();
        }
        finally
        {
            StopQuietly(process);
        }
    }

    [Test]
    public async Task Exec_runner_registers_its_process_while_it_runs_and_removes_the_entry_when_it_exits()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The exec runner test runs a POSIX shell script.");
        }

        var script = Path.Combine(Path.GetTempPath(), $"codex-ledger-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 3\n");
        try
        {
            var manager = new CliProcessManager();
            var runner = new DefaultCodexProcessRunner(null, manager, OwnerSessionId);
            var invocation = new CodexProcessInvocation("/bin/sh", [script], new Dictionary<string, string>(), string.Empty);
            var query = new CliManagedProcessQuery { ProviderName = CodexProcessLedger.ProviderName, SessionId = OwnerSessionId };

            var run = Task.Run(async () =>
            {
                await foreach (var _ in runner.RunAsync(invocation, NullLogger.Instance, CancellationToken.None))
                {
                }
            });

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (manager.GetAliveManagedProcesses(query).Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            var entry = manager.GetAliveManagedProcesses(query).Single();
            entry.ProviderName.ShouldBe("codex");
            entry.SessionId.ShouldBe(OwnerSessionId);

            await run.WaitAsync(TimeSpan.FromSeconds(30));

            manager.GetAliveManagedProcesses(query).ShouldBeEmpty();
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static Process StartLongRunning()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new ProcessStartInfo("sleep", "60");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The test process did not start.");
    }

    private static void StopQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
