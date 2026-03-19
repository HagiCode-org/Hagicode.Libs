using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Implements Codex CLI integration.
/// </summary>
public class CodexProvider : ICliProvider<CodexOptions>
{
    private const string InternalOriginatorEnvironmentVariable = "CODEX_INTERNAL_ORIGINATOR_OVERRIDE";
    private const string InternalOriginatorValue = "codex_sdk_csharp";
    private static readonly string[] DefaultExecutableCandidates = ["codex", "codex-cli"];

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public CodexProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "codex";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        CodexOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException("Unable to locate the Codex executable. Set CodexOptions.ExecutablePath or ensure 'codex' is on PATH.");

        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = options.WorkingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment)
        };

        await using var transport = CreateTransport(startContext);
        await transport.ConnectAsync(cancellationToken);
        await transport.SendAsync(CreatePromptMessage(prompt), cancellationToken);

        await foreach (var message in transport.ReceiveAsync(cancellationToken))
        {
            yield return message;

            if (IsTerminalMessage(message.Type))
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
            var executablePath = _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "Codex executable was not found. Install Codex or set CodexOptions.ExecutablePath."
                };
            }

            var result = await _processManager.ExecuteAsync(
                new ProcessStartContext
                {
                    ExecutablePath = executablePath,
                    Arguments = ["--version"],
                    EnvironmentVariables = runtimeEnvironment,
                    Timeout = TimeSpan.FromSeconds(10)
                },
                cancellationToken);

            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = result.ExitCode == 0,
                Version = result.ExitCode == 0 ? result.StandardOutput.Trim() : null,
                ErrorMessage = result.ExitCode == 0 ? null : result.StandardError.Trim()
            };
        }
        catch (Exception ex)
        {
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal virtual IReadOnlyList<string> BuildCommandArguments(CodexOptions options)
    {
        var arguments = new List<string>
        {
            "exec",
            "--experimental-json"
        };

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.AddRange(["--model", options.Model]);
        }

        if (!string.IsNullOrWhiteSpace(options.SandboxMode))
        {
            arguments.AddRange(["--sandbox", options.SandboxMode]);
        }

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            arguments.AddRange(["--cd", options.WorkingDirectory]);
        }

        foreach (var directory in options.AddDirectories)
        {
            arguments.AddRange(["--add-dir", directory]);
        }

        if (options.SkipGitRepositoryCheck)
        {
            arguments.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(options.ApprovalPolicy))
        {
            arguments.AddRange(["--config", $"approval_policy=\"{options.ApprovalPolicy}\""]);
        }

        if (!string.IsNullOrWhiteSpace(options.ThreadId))
        {
            arguments.AddRange(["resume", options.ThreadId]);
        }

        foreach (var extraArgument in options.ExtraArgs)
        {
            var flag = extraArgument.Key.StartsWith("--", StringComparison.Ordinal)
                ? extraArgument.Key
                : $"--{extraArgument.Key}";
            arguments.Add(flag);
            if (extraArgument.Value is not null)
            {
                arguments.Add(extraArgument.Value);
            }
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        CodexOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal)
        {
            [InternalOriginatorEnvironmentVariable] = InternalOriginatorValue
        };

        foreach (var entry in options.EnvironmentVariables)
        {
            environment[entry.Key] = entry.Value;
        }

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            environment["CODEX_API_KEY"] = options.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            environment["OPENAI_BASE_URL"] = options.BaseUrl;
        }

        return environment;
    }

    /// <summary>
    /// Creates the transport used for one execution.
    /// </summary>
    /// <param name="startContext">The subprocess start context.</param>
    /// <returns>The transport instance.</returns>
    protected virtual ICliTransport CreateTransport(ProcessStartContext startContext)
    {
        return new CodexExecTransport(_processManager, startContext);
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken);
    }

    private string? ResolveExecutablePath(CodexOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
    }

    private static CliMessage CreatePromptMessage(string prompt)
    {
        return new CliMessage(
            "input",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["input"] = prompt
            }));
    }

    private static bool IsTerminalMessage(string messageType)
    {
        return string.Equals(messageType, "turn.completed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(messageType, "turn.failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(messageType, "error", StringComparison.OrdinalIgnoreCase);
    }
}
