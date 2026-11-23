using System.Diagnostics;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Copilot;
using Microsoft.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// The headless Copilot CLI that a session run starts is attributed to the session in the shared ledger, and it is stopped
/// when its runtime is disposed.
/// </summary>
[TestFixture]
public sealed class CopilotOwnedCliTests
{
    private const string OwnerSessionId = "core-session-id";

    [Test]
    public async Task Owned_cli_is_registered_under_the_owner_session_and_disposing_it_stops_the_process_and_removes_the_entry()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The fake Copilot CLI is a POSIX shell script.");
        }

        var scriptDirectory = CreateScriptDirectory();
        try
        {
            var executable = WriteListeningScript(scriptDirectory, port: 47999);
            var manager = new CliProcessManager();
            var query = new CliManagedProcessQuery { ProviderName = OwnedCopilotCli.ProviderName, SessionId = OwnerSessionId };

            var owned = await OwnedCopilotCli.StartAsync(
                manager, executable, scriptDirectory, [], OwnerSessionId, TimeSpan.FromSeconds(10), CancellationToken.None);

            owned.Port.ShouldBe(47999);
            owned.ConnectionToken.ShouldNotBeNullOrWhiteSpace();
            var entry = manager.GetAliveManagedProcesses(query).Single();
            entry.ProviderName.ShouldBe("copilot");
            entry.SessionId.ShouldBe(OwnerSessionId);
            var pid = entry.Pid;
            IsRunning(pid).ShouldBeTrue();

            await owned.DisposeAsync();

            manager.GetAliveManagedProcesses(query).ShouldBeEmpty();
            IsRunning(pid).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Owned_cli_without_an_owner_session_is_not_registered_but_is_still_stopped_on_dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The fake Copilot CLI is a POSIX shell script.");
        }

        var scriptDirectory = CreateScriptDirectory();
        try
        {
            var executable = WriteListeningScript(scriptDirectory, port: 47998);
            var manager = new CliProcessManager();

            var owned = await OwnedCopilotCli.StartAsync(
                manager, executable, scriptDirectory, [], ownerSessionId: "   ", TimeSpan.FromSeconds(10), CancellationToken.None);

            manager.GetAliveManagedProcesses().ShouldBeEmpty();
            await owned.DisposeAsync();
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_cli_that_exits_before_listening_fails_the_start_with_its_output()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The fake Copilot CLI is a POSIX shell script.");
        }

        var scriptDirectory = CreateScriptDirectory();
        try
        {
            var executable = Path.Combine(scriptDirectory, "copilot-fails.sh");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\necho 'authentication required'\nexit 3\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var exception = await Should.ThrowAsync<InvalidOperationException>(() => OwnedCopilotCli.StartAsync(
                new CliProcessManager(), executable, scriptDirectory, [], OwnerSessionId, TimeSpan.FromSeconds(10), CancellationToken.None));

            exception.Message.ShouldContain("authentication required");
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Owned_runtime_connects_over_loopback_with_the_cli_token_and_no_github_client_options()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The fake Copilot CLI is a POSIX shell script.");
        }

        var scriptDirectory = CreateScriptDirectory();
        try
        {
            var executable = WriteListeningScript(scriptDirectory, port: 47997);
            var owned = await OwnedCopilotCli.StartAsync(
                null, executable, scriptDirectory, [], OwnerSessionId, TimeSpan.FromSeconds(10), CancellationToken.None);
            try
            {
                var request = CreateRequest(useLoggedInUser: true, cliUrl: null) with { CliPath = executable };

                var options = GitHubCopilotSdkGateway.BuildClientOptions(request, owned);

                options.Mode.ShouldBe(CopilotClientMode.CodingAgent);
                (options.GitHub?.UseLoggedInUser).ShouldBeNull();
                var connection = options.Runtime.ShouldBeOfType<TcpRuntimeConnection>();
                connection.Host.ShouldBe("127.0.0.1");
                connection.Port.ShouldBe(47997);
                connection.ConnectionToken.ShouldBe(owned.ConnectionToken);
            }
            finally
            {
                await owned.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(scriptDirectory, recursive: true);
        }
    }

    [Test]
    public void Owner_session_id_is_read_from_the_run_environment_and_blank_values_are_ignored()
    {
        OwnedCopilotCli.OwnerSessionIdOf(new Dictionary<string, string?> { ["HAGI_SESSION_ID"] = " abc " }).ShouldBe("abc");
        OwnedCopilotCli.OwnerSessionIdOf(new Dictionary<string, string?> { ["hagi_session_id"] = "xyz" }).ShouldBe("xyz");
        OwnedCopilotCli.OwnerSessionIdOf(new Dictionary<string, string?> { ["HAGI_SESSION_ID"] = "  " }).ShouldBeNull();
        OwnedCopilotCli.OwnerSessionIdOf(new Dictionary<string, string?>()).ShouldBeNull();
    }

    private static CopilotSdkRequest CreateRequest(bool useLoggedInUser, string? cliUrl)
    {
        return new CopilotSdkRequest(
            Prompt: "prompt",
            Model: null,
            WorkingDirectory: null,
            SessionId: null,
            CliPath: "copilot",
            CliUrl: cliUrl,
            GitHubToken: null,
            UseLoggedInUser: useLoggedInUser,
            Timeout: TimeSpan.FromSeconds(30),
            IdleTimeout: TimeSpan.FromSeconds(30),
            StartupTimeout: TimeSpan.FromSeconds(10),
            CliArgs: [],
            EnvironmentVariables: new Dictionary<string, string?>());
    }

    private static string CreateScriptDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"copilot-owned-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WriteListeningScript(string directory, int port)
    {
        var executable = Path.Combine(directory, "copilot-listening.sh");
        File.WriteAllText(executable, $"#!/bin/sh\necho \"CLI server listening on port {port}\"\nsleep 60\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executable;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
