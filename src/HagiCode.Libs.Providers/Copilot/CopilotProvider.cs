using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="CopilotProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager used for version probing.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public CopilotProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
        : this(executableResolver, processManager, new GitHubCopilotSdkGateway(), runtimeEnvironmentResolver)
    {
    }

    internal CopilotProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        ICopilotSdkGateway gateway,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "copilot";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

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
            ?? throw new FileNotFoundException("Unable to locate the Copilot executable. Set CopilotOptions.ExecutablePath or ensure 'copilot' is on PATH.");
        var sdkRequest = BuildSdkRequest(options, prompt, executablePath, runtimeEnvironment);

        foreach (var diagnostic in CopilotCliCompatibility.BuildCliArgs(options).Diagnostics)
        {
            yield return CreateDiagnosticMessage(diagnostic);
        }

        await foreach (var eventData in _gateway.SendPromptAsync(sdkRequest, cancellationToken))
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
                    ErrorMessage = "Copilot executable was not found. Install @github/copilot or set CopilotOptions.ExecutablePath."
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
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
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
        return eventData.Type switch
        {
            CopilotSdkStreamEventType.TextDelta when !string.IsNullOrWhiteSpace(eventData.Content) =>
                CreateMessage("assistant", new
                {
                    type = "assistant",
                    role = "assistant",
                    text = eventData.Content
                }),
            CopilotSdkStreamEventType.ReasoningDelta when !string.IsNullOrWhiteSpace(eventData.Content) =>
                CreateMessage("reasoning", new
                {
                    type = "reasoning",
                    text = eventData.Content
                }),
            CopilotSdkStreamEventType.ToolExecutionStart =>
                CreateMessage("tool.started", new
                {
                    type = "tool.started",
                    tool_name = eventData.ToolName,
                    tool_call_id = eventData.ToolCallId
                }),
            CopilotSdkStreamEventType.ToolExecutionEnd =>
                CreateMessage("tool.completed", new
                {
                    type = "tool.completed",
                    tool_name = eventData.ToolName,
                    tool_call_id = eventData.ToolCallId,
                    text = eventData.Content,
                    failed = LooksLikeToolFailure(eventData.Content)
                }),
            CopilotSdkStreamEventType.Error =>
                CreateMessage("error", new
                {
                    type = "error",
                    message = eventData.ErrorMessage ?? "GitHub Copilot stream failed."
                }),
            CopilotSdkStreamEventType.Completed =>
                CreateMessage("result", new
                {
                    type = "result",
                    status = "completed"
                }),
            CopilotSdkStreamEventType.RawEvent =>
                CreateMessage("event.raw", new
                {
                    type = "event.raw",
                    raw_event_type = eventData.RawEventType,
                    text = eventData.Content
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

    private static CliMessage CreateDiagnosticMessage(string message)
        => CreateMessage("diagnostic", new
        {
            type = "diagnostic",
            message
        });

    private static CliMessage CreateMessage(string type, object payload)
        => new(type, JsonSerializer.SerializeToElement(payload));
}
