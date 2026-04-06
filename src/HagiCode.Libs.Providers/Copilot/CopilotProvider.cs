using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Pooling;

namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// Implements GitHub Copilot integration through the SDK-managed Copilot session path.
/// </summary>
public class CopilotProvider : ICliProvider<CopilotOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["copilot"];

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly ICopilotSdkGateway _gateway;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly CliProviderPoolCoordinator _poolCoordinator;
    private readonly CliProviderPoolConfigurationRegistry _poolConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager used for version probing.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public CopilotProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        CliProviderPoolCoordinator? poolCoordinator = null,
        CliProviderPoolConfigurationRegistry? poolConfiguration = null)
        : this(executableResolver, processManager, new GitHubCopilotSdkGateway(), runtimeEnvironmentResolver, poolCoordinator, poolConfiguration)
    {
    }

    internal CopilotProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        ICopilotSdkGateway gateway,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        CliProviderPoolCoordinator? poolCoordinator = null,
        CliProviderPoolConfigurationRegistry? poolConfiguration = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _poolCoordinator = poolCoordinator ?? new CliProviderPoolCoordinator();
        _poolConfiguration = poolConfiguration ?? new CliProviderPoolConfigurationRegistry();
    }

    /// <inheritdoc />
    public string Name => "copilot";

    /// <inheritdoc />
    public bool IsAvailable => ResolvePreferredDiscoveredExecutablePath(environmentVariables: null) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        CopilotOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException("Unable to locate a Copilot executable. Install @github/copilot and ensure the CLI or a VS Code Copilot Chat shim is on PATH.");
        var sdkRequest = BuildSdkRequest(options, prompt, executablePath, runtimeEnvironment);
        var poolSettings = ResolvePoolSettings(options);

        foreach (var diagnostic in CopilotCliCompatibility.BuildCliArgs(options).Diagnostics)
        {
            yield return CreateDiagnosticMessage(diagnostic);
        }

        if (!poolSettings.Enabled)
        {
            await foreach (var eventData in _gateway.SendPromptAsync(sdkRequest, cancellationToken).ConfigureAwait(false))
            {
                var mappedMessage = MapEvent(eventData);
                if (mappedMessage is null)
                {
                    continue;
                }

                yield return mappedMessage;
                if (IsTerminalMessage(mappedMessage.Type))
                {
                    yield break;
                }
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
                options.AuthSource,
                options.CliUrl,
                options.Permissions,
                options.AdditionalArgs,
                options.NoAskUser,
                runtimeEnvironment,
                options.EnvironmentVariables),
            poolSettings);

        await using var lease = await _poolCoordinator.AcquireCopilotRuntimeAsync(
            request,
            async ct =>
            {
                var runtime = await _gateway.CreateRuntimeAsync(sdkRequest, ct).ConfigureAwait(false);
                var entry = new CliRuntimePoolEntry<ICopilotSdkRuntime>(Name, runtime, request.CompatibilityFingerprint, request.Settings);
                entry.RegisterKey(request.LogicalSessionKey);
                return entry;
            },
            cancellationToken).ConfigureAwait(false);

        var shouldEvictAnonymous = request.LogicalSessionKey is null && !poolSettings.KeepAnonymousSessions;
        var faulted = false;
        await lease.Entry.ExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lease.IsWarmLease)
            {
                var reusedMessage = MapEvent(new CopilotSdkStreamEvent(
                    CopilotSdkStreamEventType.SessionReused,
                    SessionId: lease.Entry.Resource.SessionId,
                    RequestedSessionId: sdkRequest.SessionId));
                if (reusedMessage is not null)
                {
                    yield return reusedMessage;
                }
            }

            await foreach (var eventData in lease.Entry.Resource.SendPromptAsync(sdkRequest, cancellationToken).ConfigureAwait(false))
            {
                var mappedMessage = MapEvent(eventData);
                if (mappedMessage is null)
                {
                    continue;
                }

                if (string.Equals(mappedMessage.Type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    faulted = true;
                }

                yield return mappedMessage;
                if (IsTerminalMessage(mappedMessage.Type))
                {
                    break;
                }
            }
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
            var executablePath = ResolvePreferredDiscoveredExecutablePath(runtimeEnvironment);
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "No Copilot executable was found. Install @github/copilot or set CopilotOptions.ExecutablePath to a resolvable CLI path."
                };
            }

            var version = await TryGetVersionAsync(executablePath, runtimeEnvironment, cancellationToken);
            if (version is not null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = true,
                    Version = version
                };
            }

            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = false,
                ErrorMessage = "Copilot executable was found, but the SDK startup path could not confirm readiness because version probing failed. Run 'copilot --version' manually to inspect the installation."
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
        await _poolCoordinator.DisposeCopilotProviderAsync(Name).ConfigureAwait(false);
    }

    internal virtual CopilotSdkRequest BuildSdkRequest(
        CopilotOptions options,
        string prompt,
        string executablePath,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(runtimeEnvironment);

        ValidateOptions(options);
        var cliArgResult = CopilotCliCompatibility.BuildCliArgs(options);

        return new CopilotSdkRequest(
            Prompt: prompt,
            Model: options.Model,
            WorkingDirectory: options.WorkingDirectory,
            SessionId: ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId),
            CliPath: executablePath,
            CliUrl: options.CliUrl,
            GitHubToken: options.AuthSource == CopilotAuthSource.GitHubToken ? options.GitHubToken : null,
            UseLoggedInUser: options.AuthSource != CopilotAuthSource.GitHubToken,
            Timeout: options.Timeout,
            StartupTimeout: options.StartupTimeout,
            CliArgs: cliArgResult.CliArgs,
            EnvironmentVariables: BuildEnvironmentVariables(options, runtimeEnvironment));
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        CopilotOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal)
        {
            ["COPILOT_INTERNAL_ORIGINATOR_OVERRIDE"] = "hagicode_libs_csharp"
        };

        foreach (var entry in options.EnvironmentVariables)
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    internal virtual string? ResolveExecutablePath(
        CopilotOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return CopilotExecutablePathPolicy.SelectPreferredPath(
                _executableResolver.ResolveExecutablePaths(options.ExecutablePath, runtimeEnvironment));
        }

        return ResolvePreferredDiscoveredExecutablePath(runtimeEnvironment);
    }

    private string? ResolvePreferredDiscoveredExecutablePath(
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var resolvedPaths = new List<string>();
        foreach (var executableCandidate in DefaultExecutableCandidates)
        {
            resolvedPaths.AddRange(_executableResolver.ResolveExecutablePaths(executableCandidate, environmentVariables));
        }

        return CopilotExecutablePathPolicy.SelectPreferredPath(resolvedPaths);
    }

    private static void ValidateOptions(CopilotOptions options)
    {
        if (options.AuthSource == CopilotAuthSource.GitHubToken && string.IsNullOrWhiteSpace(options.GitHubToken))
        {
            throw new InvalidOperationException("CopilotOptions.GitHubToken is required when AuthSource is GitHubToken.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("CopilotOptions.Timeout must be greater than zero.");
        }

        if (options.StartupTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("CopilotOptions.StartupTimeout must be greater than zero.");
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

    private async Task<string?> TryGetVersionAsync(
        string executablePath,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        CancellationToken cancellationToken)
    {
        foreach (var arguments in new[]
                 {
                     new[] { "version" },
                     new[] { "--version" },
                     new[] { "-v" }
                 })
        {
            var result = await _processManager.ExecuteAsync(
                new ProcessStartContext
                {
                    ExecutablePath = executablePath,
                    Arguments = arguments,
                    EnvironmentVariables = runtimeEnvironment,
                    Timeout = TimeSpan.FromSeconds(15)
                },
                cancellationToken);

            if (result.ExitCode == 0)
            {
                var version = result.StandardOutput.Trim();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }

        return null;
    }

    private static CliMessage? MapEvent(CopilotSdkStreamEvent eventData)
    {
        var sessionId = eventData.SessionId;
        var requestedSessionId = eventData.RequestedSessionId;

        return eventData.Type switch
        {
            CopilotSdkStreamEventType.SessionStarted =>
                CreateMessage("session.started", new Dictionary<string, object?>
                {
                    ["type"] = "session.started",
                    ["session_id"] = sessionId,
                    ["requested_session_id"] = requestedSessionId
                }),
            CopilotSdkStreamEventType.SessionResumed =>
                CreateMessage("session.resumed", new Dictionary<string, object?>
                {
                    ["type"] = "session.resumed",
                    ["session_id"] = sessionId,
                    ["requested_session_id"] = requestedSessionId
                }),
            CopilotSdkStreamEventType.SessionReused =>
                CreateMessage("session.reused", new Dictionary<string, object?>
                {
                    ["type"] = "session.reused",
                    ["session_id"] = sessionId,
                    ["requested_session_id"] = requestedSessionId,
                    ["reuse_key"] = requestedSessionId ?? sessionId
                }),
            CopilotSdkStreamEventType.TextDelta when !string.IsNullOrEmpty(eventData.Content) =>
                CreateAssistantMessage(eventData.Content, sessionId, isAuthoritativeSnapshot: false),
            CopilotSdkStreamEventType.AssistantSnapshot when !string.IsNullOrEmpty(eventData.Content) =>
                CreateAssistantMessage(eventData.Content, sessionId, isAuthoritativeSnapshot: true),
            CopilotSdkStreamEventType.ReasoningDelta when !string.IsNullOrEmpty(eventData.Content) =>
                CreateMessage("reasoning", new Dictionary<string, object?>
                {
                    ["type"] = "reasoning",
                    ["session_id"] = sessionId,
                    ["text"] = eventData.Content
                }),
            CopilotSdkStreamEventType.ToolExecutionStart =>
                CreateMessage("tool.started", new Dictionary<string, object?>
                {
                    ["type"] = "tool.started",
                    ["session_id"] = sessionId,
                    ["tool_name"] = eventData.ToolName,
                    ["tool_call_id"] = eventData.ToolCallId
                }),
            CopilotSdkStreamEventType.ToolExecutionEnd =>
                CreateMessage("tool.completed", new Dictionary<string, object?>
                {
                    ["type"] = "tool.completed",
                    ["session_id"] = sessionId,
                    ["tool_name"] = eventData.ToolName,
                    ["tool_call_id"] = eventData.ToolCallId,
                    ["text"] = eventData.Content,
                    ["failed"] = LooksLikeToolFailure(eventData.Content)
                }),
            CopilotSdkStreamEventType.Error =>
                CreateMessage("error", new Dictionary<string, object?>
                {
                    ["type"] = "error",
                    ["session_id"] = sessionId,
                    ["message"] = eventData.ErrorMessage ?? "GitHub Copilot stream failed."
                }),
            CopilotSdkStreamEventType.Completed =>
                CreateMessage("result", new Dictionary<string, object?>
                {
                    ["type"] = "result",
                    ["session_id"] = sessionId,
                    ["status"] = "completed"
                }),
            CopilotSdkStreamEventType.RawEvent =>
                CreateMessage("event.raw", new Dictionary<string, object?>
                {
                    ["type"] = "event.raw",
                    ["session_id"] = sessionId,
                    ["raw_event_type"] = eventData.RawEventType,
                    ["text"] = eventData.Content
                }),
            _ => null
        };
    }

    private static bool LooksLikeToolFailure(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.Contains("error", StringComparison.OrdinalIgnoreCase)
               || content.Contains("failed", StringComparison.OrdinalIgnoreCase)
               || content.Contains("exception", StringComparison.OrdinalIgnoreCase)
               || content.Contains("denied", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalMessage(string type)
        => string.Equals(type, "result", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "error", StringComparison.OrdinalIgnoreCase);

    private static CliMessage CreateAssistantMessage(
        string content,
        string? sessionId,
        bool isAuthoritativeSnapshot)
    {
        return CreateMessage("assistant", new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["role"] = "assistant",
            ["session_id"] = sessionId,
            ["text"] = content,
            ["is_authoritative_snapshot"] = isAuthoritativeSnapshot
        });
    }

    private CliPoolSettings ResolvePoolSettings(CopilotOptions options)
    {
        return CliPoolSettings.Merge(_poolConfiguration.GetSettings(Name), options.PoolSettings);
    }

    private static string? ResolveLogicalSessionKey(CopilotOptions options)
    {
        var sessionId = ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId);
        if (sessionId is not null)
        {
            return $"copilot-session:{sessionId}";
        }

        return null;
    }

    private static CliMessage CreateDiagnosticMessage(string message)
        => CreateMessage("diagnostic", new
        {
            type = "diagnostic",
            message
        });

    private static CliMessage CreateMessage(string type, object payload)
        => new(type, JsonSerializer.SerializeToElement(payload));
}
