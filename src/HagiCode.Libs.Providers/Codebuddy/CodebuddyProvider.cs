using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;

namespace HagiCode.Libs.Providers.Codebuddy;

/// <summary>
/// Implements CodeBuddy CLI integration over a minimal ACP session layer.
/// </summary>
public class CodebuddyProvider : ICliProvider<CodebuddyOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["codebuddy", "codebuddy-cli"];
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CliVersionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The lowest CodeBuddy CLI version verified to echo an injected steer with the message id the steer carried. Versions
    /// 2.140.0 to 2.145.0 acknowledge the steer, and some echo its text, but none echo the id, so their injection cannot be matched.
    /// </summary>
    internal static readonly Version MinimumSteerCliVersion = new(2, 146, 0);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly ConcurrentDictionary<string, Task<Version?>> _cliVersions = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="CodebuddyProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    public CodebuddyProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "codebuddy";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveProviderExecutable("Codebuddy", DefaultExecutableCandidates).IsFound;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        CodebuddyOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ValidateSteerDeliveryTimeout(options);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the CodeBuddy executable. Set CodebuddyOptions.ExecutablePath or ensure 'codebuddy' is on PATH.");

        var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = BuildCommandArguments(options),
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
            Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
        };

        await foreach (var message in ExecuteOneShotAsync(options, prompt, workingDirectory, startContext, cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var executablePath = _executableResolver.ResolveProviderExecutable("Codebuddy", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "CodeBuddy executable was not found. Install CodeBuddy or set CodebuddyOptions.ExecutablePath."
                };
            }

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = ["--acp"],
                WorkingDirectory = Directory.GetCurrentDirectory(),
                EnvironmentVariables = runtimeEnvironment,
                Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
            };

            await using var sessionClient = CreateSessionClient(startContext);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(DefaultStartupTimeout);

            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            var initializeResult = await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);

            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = DescribeInitializeResult(initializeResult)
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
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal virtual IReadOnlyList<string> BuildCommandArguments(CodebuddyOptions options)
    {
        var arguments = new List<string> { "--acp" };
        foreach (var argument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(argument);
            if (normalizedArgument is null ||
                string.Equals(normalizedArgument, "--acp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            arguments.Add(normalizedArgument);
        }

        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        CodebuddyOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal);
        foreach (var entry in options.EnvironmentVariables)
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    /// <summary>
    /// Creates the ACP session client used for a single execution.
    /// </summary>
    /// <param name="startContext">The subprocess start context.</param>
    /// <returns>The ACP session client.</returns>
    protected virtual IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
    {
        return new AcpSessionClient(CreateAcpTransport(startContext));
    }

    /// <summary>
    /// Creates the raw ACP transport used by the session client.
    /// </summary>
    /// <param name="startContext">The subprocess start context.</param>
    /// <returns>The ACP transport.</returns>
    protected virtual IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
    {
        return new SubprocessAcpTransport(_processManager, startContext);
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAsync(
        CodebuddyOptions options,
        string prompt,
        string workingDirectory,
        ProcessStartContext startContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CodebuddySteerTracker? steerTracker = null;
        try
        {
            await using var sessionClient = CreateSessionClient(startContext);
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(options.StartupTimeout ?? DefaultStartupTimeout);

            await sessionClient.ConnectAsync(startupCts.Token).ConfigureAwait(false);
            await sessionClient.InitializeAsync(startupCts.Token).ConfigureAwait(false);
            var sessionHandle = await sessionClient.StartSessionAsync(
                workingDirectory,
                options.SessionId,
                options.Model,
                startupCts.Token).ConfigureAwait(false);
            await EnsureModeAsync(sessionClient, sessionHandle.SessionId, options.ModeId, startupCts.Token).ConfigureAwait(false);

            if (options.Steering is { } steering &&
                await IsSteerableCliAsync(startContext.ExecutablePath, startupCts.Token).ConfigureAwait(false))
            {
                var steerSessionId = sessionHandle.SessionId;
                steerTracker = new CodebuddySteerTracker(
                    (steerId, text, token) => SendSteerAsync(sessionClient, steerSessionId, steerId, text, token),
                    options.SteerDeliveryTimeout);
                steering.Attach((steerId, text, token) => steerTracker.SteerAsync(steerId, text, token));
            }

            try
            {
                yield return CodebuddyAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

                await foreach (var message in StreamPromptAttemptAsync(
                                   sessionClient,
                                   sessionHandle.SessionId,
                                   sessionHandle.IsResumed,
                                   prompt,
                                   steerTracker,
                                   cancellationToken).ConfigureAwait(false))
                {
                    yield return message;
                }
            }
            finally
            {
                // Stop accepting steers before the process is disposed. Outcomes are settled after the disposal, below.
                options.Steering?.Detach();
                steerTracker?.CloseWindow();
            }
        }
        finally
        {
            if (steerTracker is not null)
            {
                foreach (var (steerId, outcome) in steerTracker.Finish())
                {
                    options.Steering!.Report(steerId, outcome);
                }
            }
        }
    }

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveExecutablePath(CodebuddyOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            return _executableResolver.ResolveExecutablePath(options.ExecutablePath, runtimeEnvironment);
        }

        return _executableResolver.ResolveProviderExecutable("Codebuddy", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
    }

    private static Task<JsonElement> SendSteerAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        string steerId,
        string text,
        CancellationToken cancellationToken)
    {
        // The raw request path is the one the session client exposes for methods outside the bootstrap set; the ACP client
        // serializes writes, and the reply is matched to its own request id.
        return sessionClient.InvokeBootstrapMethodAsync(
            "session/steer",
            new
            {
                sessionId,
                contentBlocks = new object[] { new { type = "text", text } },
                clientUserMessageId = steerId
            },
            cancellationToken);
    }

    private async Task<bool> IsSteerableCliAsync(string executablePath, CancellationToken cancellationToken)
    {
        var version = await _cliVersions
            .GetOrAdd(executablePath, path => ReadCliVersionAsync(path, CancellationToken.None))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return version is not null && version >= MinimumSteerCliVersion;
    }

    /// <summary>
    /// Reads the CLI version from <c>--version</c>. The ACP initialize result does not carry it. A failure returns <see langword="null" />,
    /// which leaves steering detached.
    /// </summary>
    protected virtual async Task<Version?> ReadCliVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executablePath, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CliVersionTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var match = Regex.Match(output, @"\d+\.\d+\.\d+");
            return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ValidateSteerDeliveryTimeout(CodebuddyOptions options)
    {
        if (options.Steering is not null && options.SteerDeliveryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SteerDeliveryTimeout,
                "SteerDeliveryTimeout must be positive.");
        }
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return Path.GetFullPath(workingDirectory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static string DescribeInitializeResult(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind == JsonValueKind.Object)
        {
            if (initializeResult.TryGetProperty("agentInfo", out var agentInfo) &&
                agentInfo.ValueKind == JsonValueKind.Object)
            {
                var name = TryGetString(agentInfo, "name");
                var version = TryGetString(agentInfo, "version");
                if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(version))
                {
                    return string.Join(" ", new[] { name, version }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                }
            }

            if (initializeResult.TryGetProperty("protocolVersion", out var protocolVersion))
            {
                return $"ACP protocol {protocolVersion}";
            }
        }

        return "ACP initialize succeeded";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static async IAsyncEnumerable<CliMessage> StreamPromptMessagesAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        Task<JsonElement> promptTask,
        bool isResumedSession,
        CodebuddySteerTracker? steerTracker,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sawAssistantText = false;
        var isHistoryReplayActive = false;
        var replayedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var receivedCompletionNotification = false;
        using var receiveUpdatesCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = CancelReceiveLoopWhenPromptCompletesAsync(promptTask, receiveUpdatesCancellation);
        await using var updateEnumerator = sessionClient.ReceiveNotificationsAsync(receiveUpdatesCancellation.Token)
            .GetAsyncEnumerator(receiveUpdatesCancellation.Token);

        while (true)
        {
            AcpNotification notification = null!;
            Exception? streamFailure = null;
            try
            {
                if (!await updateEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                notification = updateEnumerator.Current;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && promptTask.IsCompleted)
            {
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                streamFailure = new InvalidOperationException($"CodeBuddy stream ended unexpectedly: {ex.Message}", ex);
            }

            if (streamFailure is not null)
            {
                yield return CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(sessionId, streamFailure);
                yield break;
            }

            // The echo of an injected steer is consumption, not conversation output.
            if (steerTracker?.Observe(notification) == true)
            {
                continue;
            }

            if (isResumedSession &&
                CodebuddyAcpMessageMapper.TryGetReplayWindowBoundary(notification, out var isReplayStart))
            {
                isHistoryReplayActive = isReplayStart;
            }

            if (isResumedSession &&
                isHistoryReplayActive &&
                IsReplayContent(notification))
            {
                if (TryGetToolCallId(notification, out var replayedToolCallId))
                {
                    replayedToolCallIds.Add(replayedToolCallId);
                }

                continue;
            }

            if (isResumedSession &&
                !isHistoryReplayActive &&
                replayedToolCallIds.Count > 0 &&
                TryGetToolCallId(notification, out var toolCallId) &&
                replayedToolCallIds.Contains(toolCallId))
            {
                continue;
            }

            foreach (var normalizedMessage in CodebuddyAcpMessageMapper.NormalizeNotification(notification))
            {
                var message = normalizedMessage;
                if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    CodebuddyAcpMessageMapper.TryExtractMessageText(message.Content, out _))
                {
                    sawAssistantText = true;
                }

                if (string.Equals(message.Type, "terminal.completed", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var terminalPromptResult = await promptTask.ConfigureAwait(false);
                        if (CodebuddyAcpMessageMapper.IsFailurePromptResult(terminalPromptResult))
                        {
                            message = CodebuddyAcpMessageMapper.CreateTerminalMessage(sessionId, terminalPromptResult);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        message = CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(sessionId, ex);
                    }
                }

                if (IsTerminalMessage(message.Type))
                {
                    steerTracker?.CloseWindow();
                    if (string.Equals(message.Type, "terminal.failed", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return message;
                        yield break;
                    }

                    receivedCompletionNotification = true;
                    break;
                }

                yield return message;
            }

            if (receivedCompletionNotification)
            {
                break;
            }
        }

        steerTracker?.CloseWindow();

        JsonElement promptResult;
        Exception? promptFailure = null;
        try
        {
            promptResult = await promptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            promptFailure = ex;
            promptResult = default;
        }

        if (promptFailure is not null)
        {
            yield return CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(sessionId, promptFailure);
            yield break;
        }

        foreach (var fallbackMessage in BuildFallbackMessages(sessionId, promptResult, sawAssistantText, sessionClient))
        {
            yield return fallbackMessage;
        }
    }

    private static bool IsReplayContent(AcpNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object ||
            !notification.Parameters.TryGetProperty("update", out var updateElement) ||
            updateElement.ValueKind != JsonValueKind.Object ||
            !updateElement.TryGetProperty("sessionUpdate", out var updateKind) ||
            updateKind.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var updateName = updateKind.GetString();
        return string.Equals(updateName, "agent_message_chunk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(updateName, "tool_call", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(updateName, "tool_call_update", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetToolCallId(AcpNotification notification, out string toolCallId)
    {
        toolCallId = string.Empty;
        if (!TryGetSessionUpdate(notification, out var updateElement))
        {
            return false;
        }

        foreach (var propertyName in new[] { "toolCallId", "tool_call_id", "id" })
        {
            if (updateElement.TryGetProperty(propertyName, out var idElement) &&
                idElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                toolCallId = idElement.GetString()!;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSessionUpdate(AcpNotification notification, out JsonElement updateElement)
    {
        updateElement = default;
        return string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) &&
               notification.Parameters.ValueKind == JsonValueKind.Object &&
               notification.Parameters.TryGetProperty("update", out updateElement) &&
               updateElement.ValueKind == JsonValueKind.Object;
    }

    private static async Task CancelReceiveLoopWhenPromptCompletesAsync(
        Task<JsonElement> promptTask,
        CancellationTokenSource receiveUpdatesCancellation)
    {
        try
        {
            var promptResult = await promptTask.ConfigureAwait(false);
            if (!CodebuddyAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult) &&
                !receiveUpdatesCancellation.IsCancellationRequested)
            {
                TryCancelReceiveLoop(receiveUpdatesCancellation);
            }
        }
        catch
        {
            TryCancelReceiveLoop(receiveUpdatesCancellation);
        }
    }

    private static IAsyncEnumerable<CliMessage> StreamPromptAttemptAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        bool isResumedSession,
        string prompt,
        CodebuddySteerTracker? steerTracker,
        CancellationToken cancellationToken)
    {
        var promptTask = sessionClient.SendPromptAsync(sessionId, prompt, cancellationToken);
        return StreamPromptMessagesAsync(sessionClient, sessionId, promptTask, isResumedSession, steerTracker, cancellationToken);
    }

    private static IEnumerable<CliMessage> BuildFallbackMessages(
        string sessionId,
        JsonElement promptResult,
        bool sawAssistantText,
        IAcpSessionClient sessionClient)
    {
        if (!sawAssistantText &&
            CodebuddyAcpMessageMapper.TryExtractPromptResultText(promptResult, out var fallbackText) &&
            !CodebuddyAcpMessageMapper.IsFailurePromptResult(promptResult))
        {
            yield return CodebuddyAcpMessageMapper.CreateAssistantMessage(sessionId, fallbackText, promptResult);
        }

        if (!sawAssistantText)
        {
            var diagnostic = sessionClient.GetDiagnosticSummary();
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                yield return CodebuddyAcpMessageMapper.CreateTerminalFailureMessage(
                    sessionId,
                    new InvalidOperationException(diagnostic));
                yield break;
            }
        }

        yield return CodebuddyAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);
    }

    private static void TryCancelReceiveLoop(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsTerminalMessage(string messageType)
    {
        return string.Equals(messageType, "terminal.completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(messageType, "terminal.failed", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureModeAsync(
        IAcpSessionClient sessionClient,
        string sessionId,
        string? modeId,
        CancellationToken cancellationToken)
    {
        var normalizedModeId = ArgumentValueNormalizer.NormalizeOptionalValue(modeId);
        if (normalizedModeId is null)
        {
            return;
        }

        await sessionClient.SetModeAsync(sessionId, normalizedModeId, cancellationToken).ConfigureAwait(false);
    }
}