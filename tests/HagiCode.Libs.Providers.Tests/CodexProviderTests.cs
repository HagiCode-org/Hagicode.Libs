using System.Runtime.CompilerServices;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] CodexExecutableCandidates = ["codex", "codex-cli"];

    [Fact]
    public void BuildCommandArguments_includes_expected_switches()
    {
        var provider = CreateProvider();
        var arguments = provider.BuildCommandArguments(new CodexOptions
        {
            Model = "gpt-5-codex",
            SandboxMode = "workspace-write",
            WorkingDirectory = "/tmp/project",
            ApprovalPolicy = "never",
            ThreadId = "thread-123",
            SkipGitRepositoryCheck = true,
            AddDirectories = ["/tmp/project", "/tmp/shared"],
            ExtraArgs = new Dictionary<string, string?>
            {
                ["config"] = "web_search=\"disabled\"",
                ["full-auto"] = null
            }
        });

        arguments.ShouldBe(
        [
            "exec",
            "--experimental-json",
            "--model",
            "gpt-5-codex",
            "--sandbox",
            "workspace-write",
            "--cd",
            "/tmp/project",
            "--add-dir",
            "/tmp/project",
            "--add-dir",
            "/tmp/shared",
            "--skip-git-repo-check",
            "--config",
            "approval_policy=\"never\"",
            "resume",
            "thread-123",
            "--config",
            "web_search=\"disabled\"",
            "--full-auto"
        ]);
    }

    [Fact]
    public void BuildCommandArguments_trims_optional_values_and_preserves_internal_spaces()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new CodexOptions
        {
            Model = "  gpt-5 codex  ",
            WorkingDirectory = "  /tmp/my repo  ",
            ApprovalPolicy = "  on-request  ",
            ThreadId = "  thread-456  ",
            AddDirectories = ["  /tmp/shared repo  ", "   "],
            ExtraArgs = new Dictionary<string, string?>
            {
                ["config"] = "  web_search=\"disabled\"  ",
                ["notes"] = "  keep internal  spaces  ",
                ["ignored"] = "   "
            }
        });

        arguments.ShouldBe(
        [
            "exec",
            "--experimental-json",
            "--model",
            "gpt-5 codex",
            "--cd",
            "/tmp/my repo",
            "--add-dir",
            "/tmp/shared repo",
            "--config",
            "approval_policy=\"on-request\"",
            "resume",
            "thread-456",
            "--config",
            "web_search=\"disabled\"",
            "--notes",
            "keep internal  spaces"
        ]);
    }

    [Fact]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               ExecutablePath = "/custom/codex",
                               ApiKey = "token",
                               BaseUrl = "https://api.example.com",
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["CUSTOM_FLAG"] = "1"
                               }
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/codex");
        provider.LastStartContext.EnvironmentVariables!["CODEX_API_KEY"].ShouldBe("token");
        provider.LastStartContext.EnvironmentVariables["OPENAI_BASE_URL"].ShouldBe("https://api.example.com");
        provider.LastStartContext.EnvironmentVariables["CODEX_INTERNAL_ORIGINATOR_OVERRIDE"].ShouldBe("codex_sdk_csharp");
        provider.LastStartContext.EnvironmentVariables["CUSTOM_FLAG"].ShouldBe("1");
        messages.Select(static message => message.Type).ShouldBe(["item.completed", "turn.completed"]);
        provider.SentMessages.ShouldHaveSingleItem();
        provider.SentMessages[0].Content.GetProperty("input").GetString().ShouldBe("hello");
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("npx")]
    public async Task ExecuteAsync_on_windows_resolves_npm_style_short_names_to_cmd(string executableName)
    {
        using var sandbox = new DirectorySandbox();
        var resolvedExecutable = sandbox.CreateFile($"{executableName}.cmd");
        var provider = CreateProvider(
            executableResolver: new CliExecutableResolver(static () => true),
            runtimeEnvironmentResolver: new StubRuntimeEnvironmentResolver(new Dictionary<string, string?>
            {
                ["PATH"] = sandbox.RootPath,
                ["PATHEXT"] = ".EXE;.CMD;.BAT"
            }));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new CodexOptions
                           {
                               ExecutablePath = executableName
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext.ShouldNotBeNull();
        provider.LastStartContext.ExecutablePath.ShouldBe(resolvedExecutable);
        messages.Select(static message => message.Type).ShouldBe(["item.completed", "turn.completed"]);
    }

    [Fact]
    public async Task PingAsync_reports_version_when_process_succeeds()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = new ProcessResult(0, "codex 1.2.3", string.Empty)
        };
        var provider = CreateProvider(processManager: processManager);

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("codex 1.2.3");
    }

    [Fact]
    public async Task PingAsync_prefers_injected_execution_facade()
    {
        var executionFacade = new StubExecutionFacade
        {
            Result = new CliExecutionResult
            {
                Status = CliExecutionStatus.Success,
                ExitCode = 0,
                CommandPreview = "codex --version",
                StandardOutput = "codex 9.9.9",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            }
        };
        var provider = CreateProvider(executionFacade: executionFacade);

        var result = await provider.PingAsync();

        executionFacade.Requests.ShouldHaveSingleItem();
        executionFacade.Requests[0].ExecutablePath.ShouldBe("codex");
        executionFacade.Requests[0].Arguments.ShouldBe(["--version"]);
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("codex 9.9.9");
    }

    [Fact]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_validate_installed_codex_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(CodexExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Codex CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        executableName.ShouldNotBeNullOrWhiteSpace();
        executableName.ShouldBeOneOf("codex", "codex-cli");

        var provider = new CodexProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("codex");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    private static TestCodexProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        ICliExecutionFacade? executionFacade = null,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        return new TestCodexProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            runtimeEnvironmentResolver ?? new StubRuntimeEnvironmentResolver(),
            executionFacade);
    }

    private sealed class TestCodexProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        ICliExecutionFacade? executionFacade)
        : CodexProvider(executableResolver, processManager, runtimeEnvironmentResolver, executionFacade)
    {
        public ProcessStartContext? LastStartContext { get; private set; }
        public List<CliMessage> SentMessages { get; } = [];

        protected override ICliTransport CreateTransport(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            return new StubTransport(SentMessages);
        }
    }

    private sealed class StubTransport(List<CliMessage> sentMessages) : ICliTransport
    {
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new CliMessage(
                "item.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "item.completed",
                    item = new
                    {
                        type = "agent_message",
                        text = "pong"
                    }
                }));
            await Task.Yield();
            yield return new CliMessage(
                "turn.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "turn.completed",
                    usage = new
                    {
                        input_tokens = 1,
                        cached_input_tokens = 0,
                        output_tokens = 1
                    }
                }));
        }

        public Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
        {
            sentMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class MissingExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;
    }

    private sealed class StubRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        private readonly IReadOnlyDictionary<string, string?> _environment;

        public StubRuntimeEnvironmentResolver(IReadOnlyDictionary<string, string?>? environment = null)
        {
            _environment = environment ?? new Dictionary<string, string?>
            {
                ["PATH"] = "/tmp/bin"
            };
        }

        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_environment);
        }
    }

    private sealed class DirectorySandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-provider-{Guid.NewGuid():N}");

        public DirectorySandbox()
        {
            Directory.CreateDirectory(_root);
        }

        public string RootPath => _root;

        public string CreateFile(string relativePath)
        {
            var fullPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Empty);
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }

    private sealed class StubExecutionFacade : ICliExecutionFacade
    {
        public List<CliExecutionRequest> Requests { get; } = [];

        public CliExecutionResult Result { get; init; } = new()
        {
            Status = CliExecutionStatus.Success,
            ExitCode = 0,
            CommandPreview = "codex --version",
            StandardOutput = "codex 1.0.0",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow
        };

        public Task<CliExecutionResult> ExecuteAsync(CliExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }

        public async IAsyncEnumerable<CliExecutionEvent> ExecuteStreamingAsync(CliExecutionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new CliExecutionEvent
            {
                Kind = CliExecutionEventKind.Completed,
                Result = Result,
                TimestampUtc = DateTimeOffset.UtcNow
            };

            await Task.CompletedTask;
        }
    }

    private sealed class StubCliProcessManager : CliProcessManager
    {
        public ProcessResult ExecuteResult { get; init; } = new(0, "codex 1.0.0", string.Empty);

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ExecuteResult);
        }
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
