using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Pooling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Implements Codex CLI integration.
/// </summary>
public class CodexProvider : ICliProvider<CodexOptions>
{
    private const string InternalOriginatorEnvironmentVariable = "CODEX_INTERNAL_ORIGINATOR_OVERRIDE";
    private const string InternalOriginatorValue = "codex_sdk_csharp";
    private const string RetryableGenericRefusalMessage = "I'm sorry, but I cannot assist with that request.";
    private static readonly string[] DefaultExecutableCandidates = ["codex", "codex-cli"];
    private static readonly string[] RetryableReconnectMarkers =
    [
        "stream disconnected before completion: error sending request for url",
        "stream disconnected before completion: Incomplete response returned, reason: content_filter",
        "stream disconnected before completion: The server had an error processing your request. Sorry about that!"
    ];

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly ICliExecutionFacade? _executionFacade;
    private readonly CliProviderPoolCoordinator _poolCoordinator;
    private readonly CliProviderPoolConfigurationRegistry _poolConfiguration;
    private readonly ILogger<CodexProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    /// <param name="executionFacade">The optional shared execution facade used for one-shot probes.</param>
    public CodexProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        ICliExecutionFacade? executionFacade = null,
        CliProviderPoolCoordinator? poolCoordinator = null,
        CliProviderPoolConfigurationRegistry? poolConfiguration = null,
        ILogger<CodexProvider>? logger = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _executionFacade = executionFacade;
        _poolCoordinator = poolCoordinator ?? new CliProviderPoolCoordinator();
        _poolConfiguration = poolConfiguration ?? new CliProviderPoolConfigurationRegistry();
        _logger = logger ?? NullLogger<CodexProvider>.Instance;
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

        var poolSettings = ResolvePoolSettings(options);
        if (!poolSettings.Enabled)
        {
            await foreach (var message in ExecuteOneShotAsync(options, prompt, executablePath, runtimeEnvironment, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            yield break;
        }

        var logicalSessionKey = ResolveLogicalSessionKey(options);
        var poolLookupKey = ResolvePoolLookupKey(options);
        var request = new CliRuntimePoolRequest(
            Name,
            poolLookupKey,
            CliPoolFingerprintBuilder.Build(
                executablePath,
                options.WorkingDirectory,
                options.Model,
                options.SandboxMode,
                options.ApprovalPolicy,
                options.SkipGitRepositoryCheck,
                options.AddDirectories,
                options.ConfigOverrides,
                options.EnvironmentVariables,
                options.ExtraArgs,
                runtimeEnvironment),
            poolSettings);

        _logger.LogInformation(
            "Codex pool acquire start: provider={ProviderName}, logicalSessionKey={LogicalSessionKey}, poolLookupKey={PoolLookupKey}, threadId={ThreadId}, workingDirectory={WorkingDirectory}",
            Name,
            logicalSessionKey ?? "(none)",
            poolLookupKey ?? "(none)",
            ArgumentValueNormalizer.NormalizeOptionalValue(options.ThreadId) ?? "(none)",
            ArgumentValueNormalizer.NormalizeOptionalValue(options.WorkingDirectory) ?? "(none)");

        await using var lease = await _poolCoordinator.AcquireCodexThreadAsync(
            request,
            ct => Task.FromResult(new CliRuntimePoolEntry<CodexPooledThreadState>(
                Name,
                new CodexPooledThreadState
                {
                    ThreadId = ArgumentValueNormalizer.NormalizeOptionalValue(options.ThreadId)
                },
                request.CompatibilityFingerprint,
                request.Settings)),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Codex pool acquire complete: provider={ProviderName}, logicalSessionKey={LogicalSessionKey}, poolLookupKey={PoolLookupKey}, isWarmLease={IsWarmLease}, registeredKeyCount={RegisteredKeyCount}",
            Name,
            logicalSessionKey ?? "(none)",
            poolLookupKey ?? "(none)",
            lease.IsWarmLease,
            lease.Entry.RegisteredKeys.Count);

        var shouldEvictAnonymous = request.LogicalSessionKey is null && !poolSettings.KeepAnonymousSessions;
        var faulted = false;
        var lockWaitStopwatch = Stopwatch.StartNew();
        if (lease.IsWarmLease)
        {
            _logger.LogInformation(
                "Codex execution lock wait started: provider={ProviderName}, logicalSessionKey={LogicalSessionKey}, poolLookupKey={PoolLookupKey}, threadId={ThreadId}",
                Name,
                logicalSessionKey ?? "(none)",
                poolLookupKey ?? "(none)",
                ArgumentValueNormalizer.NormalizeOptionalValue(options.ThreadId) ?? "(none)");
        }

        await lease.Entry.ExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        lockWaitStopwatch.Stop();

        var effectiveOptions = options with { ThreadId = lease.Entry.Resource.ThreadId ?? options.ThreadId };
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(effectiveOptions),
            WorkingDirectory = effectiveOptions.WorkingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(effectiveOptions, runtimeEnvironment)
        };

        _logger.LogInformation(
            "Codex execution lock acquired: provider={ProviderName}, logicalSessionKey={LogicalSessionKey}, poolLookupKey={PoolLookupKey}, waitMs={WaitMs}, resumedThreadId={ThreadId}",
            Name,
            logicalSessionKey ?? "(none)",
            poolLookupKey ?? "(none)",
            lockWaitStopwatch.ElapsedMilliseconds,
            effectiveOptions.ThreadId ?? "(none)");

        var executionStopwatch = Stopwatch.StartNew();
        try
        {
            await using var transport = CreateTransport(startContext);
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await transport.SendAsync(CreatePromptMessage(prompt), cancellationToken).ConfigureAwait(false);

            await foreach (var message in transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                if (TryGetThreadId(message.Content, out var threadId))
                {
                    lease.Entry.Resource.ThreadId = threadId;
                    if (!string.IsNullOrWhiteSpace(threadId))
                    {
                        await lease.RegisterKeyAsync(BuildThreadLookupKey(threadId), cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation(
                            "Codex pool reindexed after thread acquisition: provider={ProviderName}, logicalSessionKey={LogicalSessionKey}, newThreadId={ThreadId}, registeredKeyCount={RegisteredKeyCount}",
                            Name,
                            logicalSessionKey ?? "(none)",
                            threadId,
                            lease.Entry.RegisteredKeys.Count);
                    }
                }

                if (string.Equals(message.Type, "turn.failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    faulted = true;
                }

                yield return message;
                if (IsTerminalMessageType(message.Type))
                {
                    break;
                }
            }

            executionStopwatch.Stop();
            _logger.LogInformation(
                "Codex pooled execution finished: provider={ProviderName}, logicalSessionKey={LogicalSessionKey}, threadId={ThreadId}, faulted={Faulted}, durationMs={DurationMs}",
                Name,
                logicalSessionKey ?? "(none)",
                lease.Entry.Resource.ThreadId ?? effectiveOptions.ThreadId ?? "(none)",
                faulted,
                executionStopwatch.ElapsedMilliseconds);
        }
        finally
        {
            lease.Entry.ExecutionLock.Release();
            lease.IsFaulted = faulted || shouldEvictAnonymous;
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

            var result = await ResolveExecutionFacade().ExecuteAsync(
                new CliExecutionRequest
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
                Success = result.IsSuccess,
                Version = result.IsSuccess ? result.StandardOutput.Trim() : null,
                ErrorMessage = result.IsSuccess ? null : result.StandardError.Trim()
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
    public async ValueTask DisposeAsync()
    {
        await _poolCoordinator.DisposeCodexProviderAsync(Name).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether the specified Codex event type terminates the current execution stream.
    /// </summary>
    public static bool IsTerminalMessageType(string? messageType)
    {
        return string.Equals(messageType, "turn.completed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(messageType, "turn.failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(messageType, "error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the terminal text associated with a Codex terminal event.
    /// </summary>
    public static bool TryExtractTerminalMessage(JsonElement content, out string? terminalMessage)
    {
        terminalMessage = null;
        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var messageType = typeElement.GetString();
        if (string.Equals(messageType, "turn.completed", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetCompletionText(content, out terminalMessage);
        }

        if (string.Equals(messageType, "turn.failed", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetTurnFailedMessage(content, out terminalMessage);
        }

        if (string.Equals(messageType, "error", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetErrorMessage(content, out terminalMessage);
        }

        return false;
    }

    /// <summary>
    /// Matches terminal messages that should enter the existing bounded retry flow.
    /// </summary>
    public static bool TryExtractRetryableTerminalSummary(string? terminalMessage, out string retrySummary)
    {
        retrySummary = string.Empty;
        var normalized = ArgumentValueNormalizer.NormalizeOptionalValue(terminalMessage);
        if (normalized is null)
        {
            return false;
        }

        if (string.Equals(normalized, RetryableGenericRefusalMessage, StringComparison.Ordinal) ||
            (normalized.Contains("Reconnecting...", StringComparison.Ordinal) &&
             RetryableReconnectMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal))))
        {
            retrySummary = normalized;
            return true;
        }

        return false;
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(CodexOptions options)
    {
        var arguments = new List<string>
        {
            "exec",
            "--experimental-json"
        };

        var model = ArgumentValueNormalizer.NormalizeOptionalValue(options.Model);
        if (model is not null)
        {
            arguments.AddRange(["--model", model]);
        }

        var sandboxMode = ArgumentValueNormalizer.NormalizeOptionalValue(options.SandboxMode);
        if (sandboxMode is not null)
        {
            arguments.AddRange(["--sandbox", sandboxMode]);
        }

        var workingDirectory = ArgumentValueNormalizer.NormalizeOptionalValue(options.WorkingDirectory);
        if (workingDirectory is not null)
        {
            arguments.AddRange(["--cd", workingDirectory]);
        }

        foreach (var directory in options.AddDirectories)
        {
            var normalizedDirectory = ArgumentValueNormalizer.NormalizeOptionalValue(directory);
            if (normalizedDirectory is not null)
            {
                arguments.AddRange(["--add-dir", normalizedDirectory]);
            }
        }

        if (options.SkipGitRepositoryCheck)
        {
            arguments.Add("--skip-git-repo-check");
        }

        var approvalPolicy = ArgumentValueNormalizer.NormalizeOptionalValue(options.ApprovalPolicy);
        if (approvalPolicy is not null)
        {
            arguments.AddRange(["--config", $"approval_policy=\"{approvalPolicy}\""]);
        }

        var profile = ArgumentValueNormalizer.NormalizeOptionalValue(options.Profile);
        if (profile is not null)
        {
            arguments.AddRange(["-p", profile]);
        }

        var threadId = ArgumentValueNormalizer.NormalizeOptionalValue(options.ThreadId);
        if (threadId is not null)
        {
            arguments.AddRange(["resume", threadId]);
        }

        foreach (var configOverride in NormalizeConfigOverrides(options.ConfigOverrides, "CodexOptions.ConfigOverrides"))
        {
            arguments.AddRange(["--config", configOverride]);
        }

        foreach (var extraArgument in options.ExtraArgs)
        {
            if (IsConfigArgument(extraArgument.Key))
            {
                foreach (var configOverride in NormalizeLegacyConfigOverrides(extraArgument.Value))
                {
                    arguments.AddRange(["--config", configOverride]);
                }

                continue;
            }

            var normalizedValue = ArgumentValueNormalizer.NormalizeOptionalValue(extraArgument.Value);
            if (extraArgument.Value is not null && normalizedValue is null)
            {
                continue;
            }

            var flag = extraArgument.Key.StartsWith("--", StringComparison.Ordinal)
                ? extraArgument.Key
                : $"--{extraArgument.Key}";
            arguments.Add(flag);
            if (normalizedValue is not null)
            {
                arguments.Add(normalizedValue);
            }
        }

        return arguments;
    }

    private static IEnumerable<string> NormalizeConfigOverrides(
        IReadOnlyList<string> configOverrides,
        string source)
    {
        foreach (var configOverride in configOverrides)
        {
            var normalizedValue = ArgumentValueNormalizer.NormalizeOptionalValue(configOverride);
            if (normalizedValue is null)
            {
                continue;
            }

            ValidateConfigOverrideEntry(normalizedValue, source);
            yield return normalizedValue;
        }
    }

    private static IEnumerable<string> NormalizeLegacyConfigOverrides(string? rawValue)
    {
        if (rawValue is null)
        {
            throw new InvalidOperationException(
                "CodexOptions.ExtraArgs[\"config\"] must contain a TOML assignment. Use CodexOptions.ConfigOverrides for repeated entries.");
        }

        foreach (var entry in rawValue.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var normalizedEntry = ArgumentValueNormalizer.NormalizeOptionalValue(entry);
            if (normalizedEntry is null)
            {
                continue;
            }

            ValidateConfigOverrideEntry(
                normalizedEntry,
                "CodexOptions.ExtraArgs[\"config\"]");
            yield return normalizedEntry;
        }
    }

    private static bool IsConfigArgument(string argumentKey)
    {
        return string.Equals(argumentKey, "config", StringComparison.Ordinal)
               || string.Equals(argumentKey, "--config", StringComparison.Ordinal);
    }

    private static void ValidateConfigOverrideEntry(string entry, string source)
    {
        if (entry.IndexOf('\uFEFF') >= 0 || entry.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{source} contains an invalid config override. Each --config entry must be a single TOML assignment without BOM or control characters.");
        }
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

    private CliPoolSettings ResolvePoolSettings(CodexOptions options)
    {
        return CliPoolSettings.Merge(_poolConfiguration.GetSettings(Name), options.PoolSettings);
    }

    private static string? ResolveLogicalSessionKey(CodexOptions options)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(options.LogicalSessionKey);
    }

    private static string? ResolvePoolLookupKey(CodexOptions options)
    {
        var logicalSessionKey = ResolveLogicalSessionKey(options);
        if (logicalSessionKey is not null)
        {
            return BuildLogicalLookupKey(logicalSessionKey);
        }

        var threadId = ArgumentValueNormalizer.NormalizeOptionalValue(options.ThreadId);
        if (threadId is not null)
        {
            return BuildThreadLookupKey(threadId);
        }

        // Anonymous requests stay unkeyed until the runtime returns a stable thread id.
        return null;
    }

    private static string BuildLogicalLookupKey(string logicalSessionKey)
    {
        return $"logical::{logicalSessionKey}";
    }

    private static string BuildThreadLookupKey(string threadId)
    {
        return $"thread::{threadId}";
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAsync(
        CodexOptions options,
        string prompt,
        string executablePath,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = options.WorkingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment)
        };

        await using var transport = CreateTransport(startContext);
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await transport.SendAsync(CreatePromptMessage(prompt), cancellationToken).ConfigureAwait(false);

        await foreach (var message in transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
            if (IsTerminalMessageType(message.Type))
            {
                yield break;
            }
        }
    }

    internal virtual ICliExecutionFacade ResolveExecutionFacade()
    {
        return _executionFacade ?? new CliExecutionFacade(_processManager, _runtimeEnvironmentResolver);
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

    private static bool TryGetThreadId(JsonElement content, out string? threadId)
    {
        threadId = null;
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!content.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(typeElement.GetString(), "thread.started", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!content.TryGetProperty("thread_id", out var threadIdElement) ||
            threadIdElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        threadId = threadIdElement.GetString();
        return !string.IsNullOrWhiteSpace(threadId);
    }

    private static bool TryGetCompletionText(JsonElement content, out string? completionText)
    {
        completionText = null;
        if (TryGetString(content, "result", out completionText) ||
            TryGetString(content, "final_response", out completionText))
        {
            completionText = ArgumentValueNormalizer.NormalizeOptionalValue(completionText);
            return completionText is not null;
        }

        return false;
    }

    private static bool TryGetTurnFailedMessage(JsonElement content, out string? failureMessage)
    {
        failureMessage = null;
        if (content.TryGetProperty("error", out var errorElement))
        {
            switch (errorElement.ValueKind)
            {
                case JsonValueKind.Object when TryGetString(errorElement, "message", out failureMessage):
                    failureMessage = ArgumentValueNormalizer.NormalizeOptionalValue(failureMessage);
                    return failureMessage is not null;
                case JsonValueKind.String:
                    failureMessage = ArgumentValueNormalizer.NormalizeOptionalValue(errorElement.GetString());
                    return failureMessage is not null;
            }
        }

        failureMessage = "unknown codex turn error";
        return true;
    }

    private static bool TryGetErrorMessage(JsonElement content, out string? errorMessage)
    {
        errorMessage = TryGetString(content, "message", out var message)
            ? ArgumentValueNormalizer.NormalizeOptionalValue(message)
            : "unknown codex error";
        return errorMessage is not null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }
}
