using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Transport selection in <see cref="CodexProvider"/>: the interactive path opens only when the CLI meets the floor and the
/// app-server handshake succeeds; otherwise the exec path runs with questions disabled (design D7), and the non-interactive path
/// never starts the app-server.
/// </summary>
public sealed class CodexAppServerProviderTests
{
    private const string FakeAppServerBody = """
        while IFS= read -r line; do
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
    public async Task Supported_cli_runs_the_turn_over_the_app_server_and_reports_user_questions()
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

            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executable,
                InteractiveUserInput = true,
                UserInputHandler = (_, _) => Task.FromResult<CodexUserQuestionResponse?>(null),
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions { WorkingDirectory = directory }
            });

            session.SupportsUserQuestions.ShouldBeTrue();

            var streamed = await session.Thread.RunStreamedAsync("hello");
            var events = new List<ThreadEvent>();
            await foreach (var threadEvent in streamed.Events)
            {
                events.Add(threadEvent);
            }

            session.Thread.Id.ShouldBe("thread-fake");
            events.OfType<ThreadStartedEvent>().Single().ThreadId.ShouldBe("thread-fake");
            events.OfType<TurnCompletedEvent>().Single().Result.ShouldBe("fake-answer");
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
    private static string WriteScript(string directory, string version, string appServerBody)
    {
        var scriptPath = Path.Combine(directory, "codex");
        File.WriteAllText(scriptPath, "#!/usr/bin/env bash\n" +
            "if [ \"${1-}\" = \"--version\" ]; then\n" +
            $"  printf 'codex-cli {version}\\n'\n" +
            "  exit 0\n" +
            "fi\n" +
            "if [ \"${1-}\" = \"app-server\" ]; then\n" +
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
