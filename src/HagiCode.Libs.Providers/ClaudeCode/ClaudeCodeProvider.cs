using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Pooling;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Implements Claude Code CLI integration.
/// </summary>
public class ClaudeCodeProvider : ICliProvider<ClaudeCodeOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["claude", "claude-code"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly CliProviderPoolCoordinator _poolCoordinator;
    private readonly CliProviderPoolConfigurationRegistry _poolConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public ClaudeCodeProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        CliProviderPoolCoordinator? poolCoordinator = null,
        CliProviderPoolConfigurationRegistry? poolConfiguration = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _poolCoordinator = poolCoordinator ?? new CliProviderPoolCoordinator();
        _poolConfiguration = poolConfiguration ?? new CliProviderPoolConfigurationRegistry();
    }

    /// <inheritdoc />
    public string Name => "claude-code";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        ClaudeCodeOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException("Unable to locate the Claude Code executable.");

        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = options.WorkingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
            InputEncoding = Utf8NoBom,
            OutputEncoding = Utf8NoBom,
            Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
        };

        var poolSettings = ResolvePoolSettings(options);
        if (!poolSettings.Enabled)
        {
            var debugContext = CreateDebugContext(
                ResolveLogicalSessionKey(options),
                CliPoolFingerprintBuilder.Build(
                    executablePath,
                    options.WorkingDirectory,
                    options.Model,
                    options.MaxTurns,
                    options.SystemPrompt,
                    options.AppendSystemPrompt,
                    options.AllowedTools,
                    options.DisallowedTools,
                    options.PermissionMode,
                    options.ContinueConversation,
                    options.Resume,
                    options.SessionId,
                    options.AddDirectories,
                    options.ExtraArgs,
                    options.McpServersPath,
                    options.McpServers,
                    startContext.EnvironmentVariables),
                leaseIsWarm: false);

            await foreach (var message in ExecuteOneShotAsync(prompt, options, startContext, debugContext, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            yield break;
        }

        var request = new CliRuntimePoolRequest(
            Name,
            ResolveLogicalSessionKey(options),
            CliPoolFingerprintBuilder.Build(
                executablePath,
                options.WorkingDirectory,
                options.Model,
                options.MaxTurns,
                options.SystemPrompt,
                options.AppendSystemPrompt,
                options.AllowedTools,
                options.DisallowedTools,
                options.PermissionMode,
                options.ContinueConversation,
                options.Resume,
                options.SessionId,
                options.AddDirectories,
                options.ExtraArgs,
                options.McpServersPath,
                options.McpServers,
                startContext.EnvironmentVariables),
            poolSettings);
        var runtimeFingerprint = BuildCompactFingerprint(request.CompatibilityFingerprint);
        var poolFingerprint = BuildPoolFingerprint(request.LogicalSessionKey);

        await using var lease = await _poolCoordinator.AcquireTransportAsync(
            request,
            async ct =>
            {
                var transport = CreateTransport(startContext);
                await transport.ConnectAsync(ct).ConfigureAwait(false);
                var entry = new CliRuntimePoolEntry<ICliTransport>(Name, transport, request.CompatibilityFingerprint, request.Settings);
                entry.RegisterKey(request.LogicalSessionKey);
                return entry;
            },
            cancellationToken).ConfigureAwait(false);

        var resumeMode = lease.Kind switch
        {
            CliRuntimePoolLeaseKind.WarmReuse => "resumed",
            CliRuntimePoolLeaseKind.CompatibilityReplacement => "restarted",
            _ => "started"
        };
        var shouldEvictAnonymous = request.LogicalSessionKey is null && !poolSettings.KeepAnonymousSessions;
        var faulted = false;
        var lockAcquired = false;
        await AcquireExecutionLockAsync(lease.Entry.ExecutionLock, cancellationToken).ConfigureAwait(false);
        lockAcquired = true;
        try
        {
            await foreach (var message in ExecutePooledAttemptAsync(
                               prompt,
                               options,
                               lease.Entry.Resource,
                               new ClaudeCodeDebugContext(
                                   request.LogicalSessionKey,
                                   request.LogicalSessionKey,
                                   runtimeFingerprint,
                                   poolFingerprint,
                                   resumeMode,
                                   DateTime.UtcNow),
                               cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    faulted = true;
                }

                yield return message;
                if (IsTerminalMessageType(message.Type))
                {
                    break;
                }
            }
        }
        finally
        {
            var shouldFaultLease = faulted || shouldEvictAnonymous;
            if (lockAcquired)
            {
                try
                {
                    ReleaseExecutionLock(lease.Entry.ExecutionLock);
                }
                catch (ObjectDisposedException)
                {
                    shouldFaultLease = true;
                }
            }

            lease.IsFaulted = shouldFaultLease;
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
                    ErrorMessage = "Claude Code executable was not found."
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
    public async ValueTask DisposeAsync()
    {
        await _poolCoordinator.DisposeTransportProviderAsync(Name).ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(ClaudeCodeOptions options)
    {
        var arguments = new List<string>
        {
            "--output-format",
            "stream-json",
            "--verbose",
            "--input-format",
            "stream-json"
        };

        var model = ArgumentValueNormalizer.NormalizeOptionalValue(options.Model);
        if (model is not null)
        {
            arguments.AddRange(["--model", model]);
        }

        var systemPrompt = ArgumentValueNormalizer.NormalizeOptionalValue(options.SystemPrompt);
        if (systemPrompt is not null)
        {
            arguments.AddRange(["--system-prompt", systemPrompt]);
        }

        var appendSystemPrompt = ArgumentValueNormalizer.NormalizeOptionalValue(options.AppendSystemPrompt);
        if (appendSystemPrompt is not null)
        {
            arguments.AddRange(["--append-system-prompt", appendSystemPrompt]);
        }

        if (options.MaxTurns is { } maxTurns)
        {
            arguments.AddRange(["--max-turns", maxTurns.ToString()]);
        }

        var allowedTools = options.AllowedTools
            .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
            .Where(static value => value is not null)
            .Cast<string>()
            .ToArray();
        if (allowedTools.Length > 0)
        {
            arguments.AddRange(["--allowedTools", string.Join(',', allowedTools)]);
        }

        var disallowedTools = options.DisallowedTools
            .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
            .Where(static value => value is not null)
            .Cast<string>()
            .ToArray();
        if (disallowedTools.Length > 0)
        {
            arguments.AddRange(["--disallowedTools", string.Join(',', disallowedTools)]);
        }

        var permissionMode = ArgumentValueNormalizer.NormalizeOptionalValue(options.PermissionMode);
        if (permissionMode is not null)
        {
            arguments.AddRange(["--permission-mode", permissionMode]);
        }

        if (options.ContinueConversation)
        {
            arguments.Add("--continue");
        }

        var resume = ArgumentValueNormalizer.NormalizeOptionalValue(options.Resume);
        if (resume is not null)
        {
            arguments.AddRange(["--resume", resume]);
        }

        var sessionId = ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId);
        if (sessionId is not null && !options.ContinueConversation && resume is null)
        {
            arguments.AddRange(["--session-id", sessionId]);
        }

        foreach (var directory in options.AddDirectories)
        {
            var normalizedDirectory = ArgumentValueNormalizer.NormalizeOptionalValue(directory);
            if (normalizedDirectory is not null)
            {
                arguments.AddRange(["--add-dir", normalizedDirectory]);
            }
        }

        var mcpServersPath = ArgumentValueNormalizer.NormalizeOptionalValue(options.McpServersPath);
        if (mcpServersPath is not null)
        {
            arguments.AddRange(["--mcp-config", mcpServersPath]);
        }
        else if (options.McpServers.Count > 0)
        {
            arguments.AddRange(["--mcp-config", JsonSerializer.Serialize(new { mcpServers = options.McpServers })]);
        }

        foreach (var extraArgument in options.ExtraArgs)
        {
            var normalizedValue = ArgumentValueNormalizer.NormalizeOptionalValue(extraArgument.Value);
            if (extraArgument.Value is not null && normalizedValue is null)
            {
                continue;
            }

            arguments.Add($"--{extraArgument.Key}");
            if (normalizedValue is not null)
            {
                arguments.Add(normalizedValue);
            }
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        ClaudeCodeOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal)
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = "sdk-csharp"
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            environment["ANTHROPIC_AUTH_TOKEN"] = options.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            environment["ANTHROPIC_BASE_URL"] = options.BaseUrl;
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
        return new SubprocessTransport(_processManager, startContext);
    }

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Waits for the pooled execution lock.
    /// </summary>
    protected virtual Task AcquireExecutionLockAsync(SemaphoreSlim executionLock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionLock);
        return executionLock.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Releases the pooled execution lock.
    /// </summary>
    protected virtual void ReleaseExecutionLock(SemaphoreSlim executionLock)
    {
        ArgumentNullException.ThrowIfNull(executionLock);
        executionLock.Release();
    }

    private CliPoolSettings ResolvePoolSettings(ClaudeCodeOptions options)
    {
        return CliPoolSettings.Merge(_poolConfiguration.GetSettings(Name), options.PoolSettings);
    }

    private static string? ResolveLogicalSessionKey(ClaudeCodeOptions options)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId)
               ?? ArgumentValueNormalizer.NormalizeOptionalValue(options.Resume);
    }

    private static string? ResolveContinuationTarget(ClaudeCodeOptions options)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(options.Resume)
               ?? ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId);
    }

    private static bool CanRetryInSameContext(ClaudeCodeOptions options)
    {
        return !string.IsNullOrWhiteSpace(ResolveContinuationTarget(options));
    }

    private static bool IsRetryableTerminalFailure(CliMessage message)
    {
        return string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalMessageType(string? messageType)
    {
        return string.Equals(messageType, "result", StringComparison.OrdinalIgnoreCase)
               || string.Equals(messageType, "error", StringComparison.OrdinalIgnoreCase);
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAsync(
        string prompt,
        ClaudeCodeOptions options,
        ProcessStartContext startContext,
        ClaudeCodeDebugContext debugContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in ExecuteOneShotAttemptAsync(
                           prompt,
                           options,
                           startContext,
                           debugContext,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAttemptAsync(
        string prompt,
        ClaudeCodeOptions options,
        ProcessStartContext startContext,
        ClaudeCodeDebugContext debugContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var transport = CreateTransport(startContext);
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await transport.SendAsync(CreatePromptMessage(prompt, options), cancellationToken).ConfigureAwait(false);

        await foreach (var message in transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return EnrichMessageWithDebugMetadata(message, debugContext);
            if (IsTerminalMessageType(message.Type))
            {
                yield break;
            }
        }
    }

    private async IAsyncEnumerable<CliMessage> ExecutePooledAttemptAsync(
        string prompt,
        ClaudeCodeOptions options,
        ICliTransport transport,
        ClaudeCodeDebugContext debugContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await transport.SendAsync(CreatePromptMessage(prompt, options), cancellationToken).ConfigureAwait(false);

        await foreach (var message in transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return EnrichMessageWithDebugMetadata(message, debugContext);
            if (IsTerminalMessageType(message.Type))
            {
                yield break;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken);
    }

    private string? ResolveExecutablePath(ClaudeCodeOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
    }

    private static CliMessage CreatePromptMessage(string prompt, ClaudeCodeOptions options)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = prompt
            },
            ["session_id"] = options.SessionId
        });

        return new CliMessage("user", payload);
    }

    private static ClaudeCodeDebugContext CreateDebugContext(
        string? logicalSessionKey,
        string compatibilityFingerprint,
        bool leaseIsWarm)
    {
        var runtimeFingerprint = BuildCompactFingerprint(compatibilityFingerprint);
        return new ClaudeCodeDebugContext(
            logicalSessionKey,
            logicalSessionKey,
            runtimeFingerprint,
            BuildPoolFingerprint(logicalSessionKey),
            leaseIsWarm ? "resumed" : "started",
            DateTime.UtcNow);
    }

    private static CliMessage EnrichMessageWithDebugMetadata(CliMessage message, ClaudeCodeDebugContext debugContext)
    {
        if (message.Content.ValueKind != JsonValueKind.Object)
        {
            return message;
        }

        var rootNode = JsonNode.Parse(message.Content.GetRawText()) as JsonObject;
        if (rootNode is null)
        {
            return message;
        }

        UpsertString(rootNode, "requested_session_id", debugContext.RequestedSessionId);
        UpsertString(rootNode, "binding_key", debugContext.BindingKey);
        UpsertString(rootNode, "runtime_fingerprint", debugContext.RuntimeFingerprint);
        UpsertString(rootNode, "pool_fingerprint", debugContext.PoolFingerprint);
        UpsertString(rootNode, "resume_mode", debugContext.ResumeMode);
        UpsertString(rootNode, "event_timestamp", debugContext.EventTimestampUtc.ToString("O"));

        return message with
        {
            Content = JsonSerializer.SerializeToElement(rootNode)
        };
    }

    private static string BuildCompactFingerprint(string rawFingerprint)
    {
        var normalizedFingerprint = string.IsNullOrWhiteSpace(rawFingerprint) ? "(empty)" : rawFingerprint;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedFingerprint));
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }

    private static string BuildPoolFingerprint(string? logicalSessionKey)
    {
        return string.IsNullOrWhiteSpace(logicalSessionKey) ? "anonymous" : logicalSessionKey.Trim();
    }

    private static void UpsertString(JsonObject rootNode, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rootNode[propertyName] = value.Trim();
        }
    }

    private sealed record ClaudeCodeDebugContext(
        string? RequestedSessionId,
        string? BindingKey,
        string RuntimeFingerprint,
        string PoolFingerprint,
        string ResumeMode,
        DateTime EventTimestampUtc);
}
