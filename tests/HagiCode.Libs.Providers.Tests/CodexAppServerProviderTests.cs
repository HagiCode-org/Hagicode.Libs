using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Transport selection in <see cref="CodexProvider"/>: the interactive path opens only when the CLI meets the floor and the
/// app-server handshake succeeds; otherwise the exec path runs, and a steering-only interactive path does not enable agent
/// questions.
/// </summary>
public sealed class CodexAppServerProviderTests
{
    private const string FakeAppServerBody = """
        while IFS= read -r line; do
          if [ -n "${HAGICODE_APP_SERVER_LOG-}" ]; then printf '%s\n' "$line" >> "$HAGICODE_APP_SERVER_LOG"; fi
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
          case "$line" in
            *'"method":"initialize"'*)
              printf '{"id":%s,"result":{"userAgent":"fake-app-server"}}\n' "$id" ;;
            *'"method":"experimentalFeature/list"'*)
              printf '{"id":%s,"result":{"data":[{"name":"default_mode_request_user_input"}],"nextCursor":null}}\n' "$id" ;;
            *'"method":"thread/start"'*)
              printf '{"id":%s,"result":{"thread":{"id":"thread-fake"}}}\n' "$id" ;;
            *'"method":"turn/start"'*)
              printf '{"id":%s,"result":{"turn":{"id":"turn-fake","status":"inProgress","items":[]}}}\n' "$id"
              printf '%s\n' '{"method":"turn/started","params":{"threadId":"thread-fake","turn":{"id":"turn-fake","status":"inProgress","items":[]}}}'
              printf '%s\n' '{"method":"item/completed","params":{"threadId":"thread-fake","turnId":"turn-fake","item":{"type":"agentMessage","id":"msg-1","text":"fake-answer"}}}'
              printf '%s\n' '{"method":"turn/completed","params":{"threadId":"thread-fake","turn":{"id":"turn-fake","status":"completed","items":[],"error":null}}}' ;;
          esac
        done

        """;

    [Test]
    public async Task Steering_selects_app_server_without_enabling_agent_questions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var protocolLog = Path.Combine(directory, "app-server-requests.jsonl");
            var noQuestionFeature = FakeAppServerBody.Replace(
                "[{\"name\":\"default_mode_request_user_input\"}]",
                "[]",
                StringComparison.Ordinal);
            var executable = WriteScript(directory, "0.163.0", noQuestionFeature, protocolLog);
            var steering = new CodexRunSteering();
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);

            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                Steering = steering,
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            session.SupportsSteering.ShouldBeTrue();
            session.SupportsUserQuestions.ShouldBeFalse();

            var streamed = await session.Thread.RunStreamedAsync("hello");
            await foreach (var _ in streamed.Events)
            {
            }

            var requests = File.ReadAllLines(protocolLog);
            requests.ShouldNotContain(line => line.Contains("experimentalFeature/list", StringComparison.Ordinal));
            var threadStart = requests.Single(line => line.Contains("\"method\":\"thread/start\"", StringComparison.Ordinal));
            threadStart.ShouldNotContain("default_mode_request_user_input");
            (await steering.SteerAsync("after-completion", "not delivered")).ShouldBe(CodexSteerResult.NotSteerable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Steering_is_unavailable_when_the_cli_uses_the_exec_transport()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var marker = Path.Combine(directory, "app-server-started");
            var executable = WriteScript(directory, "0.162.9", $"echo started >> '{marker}'\n" + FakeAppServerBody);
            var steering = new CodexRunSteering();
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);

            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                Steering = steering,
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            session.SupportsSteering.ShouldBeFalse();
            (await steering.SteerAsync("unsupported", "queue this")).ShouldBe(CodexSteerResult.NotSteerable);
            File.Exists(marker).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Steering_delivery_timeout_must_be_positive_and_finite()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var executable = WriteScript(directory, "0.163.0", FakeAppServerBody);
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);
            foreach (var invalidTimeout in new[] { TimeSpan.Zero, Timeout.InfiniteTimeSpan })
            {
                await Should.ThrowAsync<ArgumentOutOfRangeException>(() => provider.CreateSessionAsync(new CodexSessionOptions
                {
                    ExecutablePath = executable,
                    Steering = new CodexRunSteering(),
                    SteerDeliveryTimeout = invalidTimeout
                }));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Steering_reaches_its_active_turn_before_completion_and_isolated_sessions_sharing_a_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        var protocolLog = Path.Combine(directory, "app-server-requests.jsonl");
        var releaseTurns = Path.Combine(directory, "release-turns");
        CodexSessionHandle? firstSession = null;
        CodexSessionHandle? secondSession = null;
        Task? firstRun = null;
        Task? secondRun = null;
        try
        {
            var executable = WriteScript(directory, "0.163.0", CreateSteeringServerBody(protocolLog, releaseTurns));
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);
            var firstSteering = new CodexRunSteering();
            var secondSteering = new CodexRunSteering();
            var firstOutcomes = new List<CodexSteerOutcome>();
            var firstOutcomeObserved = new TaskCompletionSource<CodexSteerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            firstSteering.OutcomeObserved += (_, args) =>
            {
                firstOutcomes.Add(args.Outcome);
                firstOutcomeObserved.TrySetResult(args.Outcome);
            };

            firstSession = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                Steering = firstSteering,
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });
            secondSession = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                Steering = secondSteering,
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStream = await firstSession.Thread.RunStreamedAsync("initial prompt A");
            var secondStream = await secondSession.Thread.RunStreamedAsync("initial prompt B");
            firstRun = CollectUntilTurnCompletesAsync(firstStream.Events, firstStarted);
            secondRun = CollectUntilTurnCompletesAsync(secondStream.Events, secondStarted);
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));

            (await firstSteering.SteerAsync("steer-A", "apply the first session's instruction"))
                .ShouldBe(CodexSteerResult.Sent);
            (await firstOutcomeObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(CodexSteerOutcome.Consumed);
            firstOutcomes.ShouldBe([CodexSteerOutcome.Consumed]);
            firstRun.IsCompleted.ShouldBeFalse();
            secondRun.IsCompleted.ShouldBeFalse();

            var requests = File.ReadAllLines(protocolLog);
            var initializePids = requests
                .Where(line => line.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
                .Select(line => line.Split('|', 2)[0])
                .ToArray();
            initializePids.Length.ShouldBe(2);
            initializePids[0].ShouldNotBe(initializePids[1]);

            var steerLines = requests.Where(line => line.Contains("\"method\":\"turn/steer\"", StringComparison.Ordinal)).ToArray();
            steerLines.Length.ShouldBe(1);
            steerLines[0].Split('|', 2)[0].ShouldBe(initializePids[0]);
            steerLines[0].ShouldContain("\"threadId\":\"thread-shared\"");
            steerLines[0].ShouldContain("\"expectedTurnId\":\"turn-shared\"");
            steerLines[0].ShouldContain("\"clientUserMessageId\":\"steer-A\"");

            requests.Count(line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal)).ShouldBe(2);
            File.WriteAllText(releaseTurns, "release");
            await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            File.WriteAllText(releaseTurns, "release");
            if (secondSession is not null)
            {
                await secondSession.DisposeAsync();
            }

            if (firstSession is not null)
            {
                await firstSession.DisposeAsync();
            }

            if (firstRun is not null)
            {
                try { await firstRun.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            }

            if (secondRun is not null)
            {
                try { await secondRun.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CollectUntilTurnCompletesAsync(
        IAsyncEnumerable<ThreadEvent> events,
        TaskCompletionSource turnStarted)
    {
        await foreach (var threadEvent in events)
        {
            if (threadEvent is TurnStartedEvent)
            {
                turnStarted.TrySetResult();
            }
        }
    }

    private static string CreateSteeringServerBody(string protocolLog, string releaseTurns)
    {
        return $$$$"""
            while IFS= read -r line; do
              printf '%s|%s\n' "$$" "$line" >> '{{{{protocolLog}}}}'
              id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
              case "$line" in
                *'"method":"initialize"'*)
                  printf '{"id":%s,"result":{"userAgent":"fake-app-server"}}\n' "$id" ;;
                *'"method":"thread/start"'*)
                  printf '{"id":%s,"result":{"thread":{"id":"thread-shared"}}}\n' "$id" ;;
                *'"method":"turn/start"'*)
                  printf '{"id":%s,"result":{"turn":{"id":"turn-shared","status":"inProgress","items":[]}}}\n' "$id"
                  printf '%s\n' '{"method":"turn/started","params":{"threadId":"thread-shared","turn":{"id":"turn-shared","status":"inProgress","items":[]}}}'
                  (
                    while [ ! -f '{{{{releaseTurns}}}}' ]; do sleep 0.01; done
                    printf '%s\n' '{"method":"turn/completed","params":{"threadId":"thread-shared","turn":{"id":"turn-shared","status":"completed","items":[],"error":null}}}'
                  ) & ;;
                *'"method":"turn/steer"'*)
                  printf '{"id":%s,"result":{"turnId":"turn-shared"}}\n' "$id" ;;
              esac
            done
            """;
    }

    [Test]
    public async Task No_steering_channel_keeps_thread_continuity_without_extra_processes_turns_or_prompt_retries()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var protocolLog = Path.Combine(directory, "app-server-requests.jsonl");
            var processLog = Path.Combine(directory, "app-server-processes.txt");
            var executable = WriteScript(directory, "0.163.0", FakeAppServerBody, protocolLog, processLog);
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);

            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                InteractiveUserInput = true,
                UserInputHandler = (_, _) => Task.FromResult<CodexUserQuestionResponse?>(null),
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            session.SupportsUserQuestions.ShouldBeTrue();

            for (var run = 0; run < 2; run++)
            {
                var streamed = await session.Thread.RunStreamedAsync($"hello {run}");
                var events = new List<ThreadEvent>();
                await foreach (var threadEvent in streamed.Events)
                {
                    events.Add(threadEvent);
                }

                session.Thread.Id.ShouldBe("thread-fake");
                events.OfType<ThreadStartedEvent>().Single().ThreadId.ShouldBe("thread-fake");
                events.OfType<TurnCompletedEvent>().Single().Result.ShouldBe("fake-answer");
            }

            var requests = File.ReadAllLines(protocolLog);
            File.ReadAllLines(processLog).Length.ShouldBe(1);
            requests.Count(line => line.Contains("\"method\":\"thread/start\"", StringComparison.Ordinal)).ShouldBe(1);
            requests.Count(line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal)).ShouldBe(2);
            requests.Count(line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal)
                && line.Contains("hello 0", StringComparison.Ordinal)).ShouldBe(1);
            requests.Count(line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal)
                && line.Contains("hello 1", StringComparison.Ordinal)).ShouldBe(1);
            requests.ShouldNotContain(line => line.Contains("\"method\":\"turn/steer\"", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Cli_below_the_floor_runs_exec_and_never_starts_the_app_server()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var marker = Path.Combine(directory, "app-server-started");
            var executable = WriteScript(directory, "0.162.9", $"echo started >> '{marker}'\n" + FakeAppServerBody);
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);

            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                InteractiveUserInput = true,
                UserInputHandler = (_, _) => Task.FromResult<CodexUserQuestionResponse?>(null),
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            session.SupportsUserQuestions.ShouldBeFalse();
            File.Exists(marker).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Failed_handshake_falls_back_to_the_exec_transport()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var executable = WriteScript(directory, "0.163.0", "exit 1");
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);

            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                InteractiveUserInput = true,
                UserInputHandler = (_, _) => Task.FromResult<CodexUserQuestionResponse?>(null),
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            session.SupportsUserQuestions.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Non_interactive_session_never_starts_the_app_server()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var marker = Path.Combine(directory, "app-server-started");
            var executable = WriteScript(directory, "0.163.0", $"echo started >> '{marker}'\n" + FakeAppServerBody);
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);

            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                InteractiveUserInput = false,
                UserInputHandler = (_, _) => Task.FromResult<CodexUserQuestionResponse?>(null),
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            session.SupportsUserQuestions.ShouldBeFalse();
            File.Exists(marker).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Question_timeout_must_be_positive_or_infinite()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateDirectory();
        try
        {
            var executable = WriteScript(directory, "0.163.0", FakeAppServerBody);
            var provider = new CodexProvider(new CliExecutableResolver(), logger: NullLogger<CodexProvider>.Instance);

            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                InteractiveUserInput = true,
                UserInputHandler = (_, _) => Task.FromResult<CodexUserQuestionResponse?>(null),
                QuestionTimeout = TimeSpan.Zero,
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-appserver-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Writes a POSIX stand-in for the codex CLI. <c>--version</c> reports <paramref name="version"/>; <c>app-server</c> runs
    /// <paramref name="appServerBody"/>.
    /// </summary>
    private static string WriteScript(
        string directory,
        string version,
        string appServerBody,
        string? protocolLog = null,
        string? processLog = null)
    {
        var scriptPath = Path.Combine(directory, "codex");
        File.WriteAllText(scriptPath, "#!/usr/bin/env bash\n" +
            "if [ \"${1-}\" = \"--version\" ]; then\n" +
            $"  printf 'codex-cli {version}\\n'\n" +
            "  exit 0\n" +
            "fi\n" +
            "if [ \"${1-}\" = \"app-server\" ]; then\n" +
            (processLog is null ? string.Empty : $"  printf '%s\\n' \"$$\" >> '{processLog}'\n") +
            (protocolLog is null ? string.Empty : $"  export HAGICODE_APP_SERVER_LOG='{protocolLog}'\n") +
            appServerBody + "\n" +
            "  exit 0\n" +
            "fi\n" +
            "exit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }
}
