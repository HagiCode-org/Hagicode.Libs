using System.Runtime.CompilerServices;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Providers.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexProviderTests
{
    [Fact]
    public async Task CreateSessionAsync_runs_sdk_thread_against_resolved_cli_process()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var executablePath = CreatePosixCodexScript();
        var resolver = new RecordingCliExecutableResolver(executablePath);
        var runtimeResolver = new FixedRuntimeEnvironmentResolver(new Dictionary<string, string?>
        {
            ["RUNTIME_ENV"] = "runtime-value",
            ["EXPLICIT_ENV"] = "runtime-fallback"
        });
        var provider = new CodexProvider(resolver, runtimeResolver, logger: NullLogger<CodexProvider>.Instance);

        try
        {
            await using var session = await provider.CreateSessionAsync(new CodexSessionOptions
            {
                ExecutablePath = executablePath,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["EXPLICIT_ENV"] = "from-options"
                },
                ThreadOptions = new ManagedCode.CodexSharpSDK.Client.ThreadOptions
                {
                    Model = "gpt-5-codex",
                    WorkingDirectory = "/repo/worktree"
                }
            });

            var result = await session.Thread.RunAsync("hello-codex");

            resolver.LastExecutableName.ShouldBe(executablePath);
            resolver.LastEnvironment.ShouldNotBeNull();
            resolver.LastEnvironment["RUNTIME_ENV"].ShouldBe("runtime-value");
            session.Thread.Id.ShouldBe("thread-script");
            result.FinalResponse.ShouldBe("env:runtime-value|explicit:from-options|input:hello-codex");
            result.Usage.ShouldNotBeNull();
            result.Usage.InputTokens.ShouldBe(1);
            result.Usage.OutputTokens.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(executablePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSessionAsync_throws_when_executable_cannot_be_resolved()
    {
        var provider = new CodexProvider(new RecordingCliExecutableResolver(null), logger: NullLogger<CodexProvider>.Instance);

        var exception = await Should.ThrowAsync<FileNotFoundException>(() => provider.CreateSessionAsync(new CodexSessionOptions()));

        exception.Message.ShouldContain("Unable to locate the Codex executable");
    }

    [Fact]
    public async Task PingAsync_uses_resolved_executable_and_runtime_environment()
    {
        var resolver = new RecordingCliExecutableResolver("/tools/codex");
        var runtimeResolver = new FixedRuntimeEnvironmentResolver(new Dictionary<string, string?>
        {
            ["PATH"] = "/tools",
            ["CODEX_ENV"] = "1"
        });
        var executionFacade = new RecordingCliExecutionFacade(new CliExecutionResult
        {
            Status = CliExecutionStatus.Success,
            CommandPreview = "codex --version",
            StandardOutput = "codex-test-1.2.3\n",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        var provider = new CodexProvider(resolver, runtimeResolver, executionFacade, NullLogger<CodexProvider>.Instance);

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("codex-test-1.2.3");
        resolver.LastCandidateNames.ShouldBe(["codex", "codex-cli"]);
        resolver.LastEnvironment.ShouldNotBeNull();
        resolver.LastEnvironment["PATH"].ShouldBe("/tools");
        executionFacade.LastRequest.ShouldNotBeNull();
        executionFacade.LastRequest.ExecutablePath.ShouldBe("/tools/codex");
        executionFacade.LastRequest.Arguments.ShouldBe(["--version"]);
        executionFacade.LastRequest.EnvironmentVariables.ShouldNotBeNull();
        executionFacade.LastRequest.EnvironmentVariables["CODEX_ENV"].ShouldBe("1");
        executionFacade.LastRequest.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    private static string CreatePosixCodexScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-provider-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "codex");
        File.WriteAllText(scriptPath, "#!/usr/bin/env bash\n" +
            "set -euo pipefail\n" +
            "if [ \"${1-}\" = \"--version\" ]; then\n" +
            "  printf 'codex-test-1.2.3\\n'\n" +
            "  exit 0\n" +
            "fi\n" +
            "IFS= read -r INPUT || true\n" +
            "printf '%s\\n' '{\"type\":\"thread.started\",\"thread_id\":\"thread-script\"}'\n" +
            "printf '{\"type\":\"item.completed\",\"item\":{\"id\":\"msg-1\",\"type\":\"agent_message\",\"text\":\"env:%s|explicit:%s|input:%s\"}}\\n' \"${RUNTIME_ENV:-missing}\" \"${EXPLICIT_ENV:-missing}\" \"$INPUT\"\n" +
            "printf '%s\\n' '{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1}}'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return scriptPath;
    }

    private sealed class FixedRuntimeEnvironmentResolver(IReadOnlyDictionary<string, string?> environment) : HagiCode.Libs.Core.Environment.IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(environment);
        }
    }

    private sealed class RecordingCliExecutableResolver(string? resolvedPath) : CliExecutableResolver
    {
        public IReadOnlyDictionary<string, string?>? LastEnvironment { get; private set; }

        public IReadOnlyList<string>? LastCandidateNames { get; private set; }

        public string? LastExecutableName { get; private set; }

        public override string? ResolveExecutablePath(
            string? executableName,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            LastExecutableName = executableName;
            LastEnvironment = environmentVariables is null
                ? null
                : new Dictionary<string, string?>(environmentVariables, StringComparer.Ordinal);
            return resolvedPath;
        }

        public override string? ResolveFirstAvailablePath(
            IEnumerable<string> executableNames,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            LastCandidateNames = executableNames.ToArray();
            LastEnvironment = environmentVariables is null
                ? null
                : new Dictionary<string, string?>(environmentVariables, StringComparer.Ordinal);
            return resolvedPath;
        }
    }

    private sealed class RecordingCliExecutionFacade(CliExecutionResult result) : ICliExecutionFacade
    {
        public CliExecutionRequest? LastRequest { get; private set; }

        public Task<CliExecutionResult> ExecuteAsync(CliExecutionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }

        public async IAsyncEnumerable<CliExecutionEvent> ExecuteStreamingAsync(
            CliExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await Task.Yield();
            yield break;
        }
    }
}
