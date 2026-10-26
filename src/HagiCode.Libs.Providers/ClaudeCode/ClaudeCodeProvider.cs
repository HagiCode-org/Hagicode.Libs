using System.Collections.Concurrent;
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
using HagiCode.Libs.Core.AgentWork;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Implements Claude Code CLI integration.
/// </summary>
public class ClaudeCodeProvider : ICliProvider<ClaudeCodeOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["claude", "claude-code"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The set of effort levels accepted by the Claude Code <c>--effort</c> flag.
    /// Values are compared case-insensitively; the lowercase canonical form is emitted to the CLI.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedEffortLevels = new HashSet<string>(
        ["low", "medium", "high", "xhigh", "max"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The set of display modes accepted by the hidden Claude Code <c>--thinking-display</c> flag.
    /// Values are compared case-insensitively; the lowercase canonical form is emitted to the CLI.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedThinkingDisplayModes = new HashSet<string>(
        ["summarized", "omitted", "highlights"],
        StringComparer.OrdinalIgnoreCase);

    private const string ThinkingDisplayFlag = "--thinking-display";

    // How long the end of a run waits for the deny that a cancelled question writes to reach the CLI.
    private static readonly TimeSpan QuestionDrainTimeout = TimeSpan.FromSeconds(5);

    // Executables that refused --thinking-display at launch. Process lifetime only, so a host restart retries the flag
    // after an in-place CLI upgrade.
    private static readonly ConcurrentDictionary<string, bool> ExecutablesRejectingThinkingDisplay = new(StringComparer.Ordinal);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly ILogger<ClaudeCodeProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeProvider" /> class.
    /// </summary>
    /// <param name="executableResolver">The executable resolver.</param>
    /// <param name="processManager">The process manager.</param>
    /// <param name="runtimeEnvironmentResolver">The optional runtime environment resolver.</param>
    /// <param name="logger">The optional logger for launch and init diagnostics. Defaults to a logger that discards entries.</param>
    public ClaudeCodeProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        ILogger<ClaudeCodeProvider>? logger = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _logger = logger ?? NullLogger<ClaudeCodeProvider>.Instance;
    }

    /// <inheritdoc />
    public string Name => "claude-code";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveProviderExecutable("ClaudeCode", DefaultExecutableCandidates).IsFound;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        ClaudeCodeOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ValidateImages(options.Images);
        if (options.SubagentBackgroundWorkTimeout != Timeout.InfiniteTimeSpan
            && options.SubagentBackgroundWorkTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SubagentBackgroundWorkTimeout,
                "SubagentBackgroundWorkTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.BackgroundWorkGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.BackgroundWorkGracePeriod,
                "BackgroundWorkGracePeriod must be positive.");
        }

        if (options.UntimedBackgroundCommandTimeout != Timeout.InfiniteTimeSpan
            && options.UntimedBackgroundCommandTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.UntimedBackgroundCommandTimeout,
                "UntimedBackgroundCommandTimeout must be zero, positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.PromptStartTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PromptStartTimeout,
                "PromptStartTimeout must be positive.");
        }

        if (options.NotificationTurnTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.NotificationTurnTimeout,
                "NotificationTurnTimeout must be positive.");
        }

        if (options.QuestionTimeout != Timeout.InfiniteTimeSpan && options.QuestionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.QuestionTimeout,
                "QuestionTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.SteerDeliveryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SteerDeliveryTimeout,
                "SteerDeliveryTimeout must be positive.");
        }

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
        var lookup = CliLaunchLookup.Resolve(_executableResolver, "ClaudeCode", DefaultExecutableCandidates, options.ExecutablePath, runtimeEnvironment);
        var executablePath = lookup.ResolvedPath
            ?? throw new FileNotFoundException(CliExecutableNotFoundMessage.Describe(
                "Unable to locate the Claude Code executable.",
                "ClaudeCode",
                lookup));

        var arguments = BuildCommandArguments(options);
        if (ExecutablesRejectingThinkingDisplay.ContainsKey(executablePath))
        {
            arguments = RemoveThinkingDisplayArguments(arguments);
        }

        var startContext = new ProcessStartContext
        {
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = ArgumentValueNormalizer.NormalizeOptionalValue(options.WorkingDirectory),
            EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
            InputEncoding = Utf8NoBom,
            OutputEncoding = Utf8NoBom,
            Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
        };

        var debugContext = CreateDebugContext(options, startContext);
        await foreach (var message in ExecuteOneShotAsync(prompt, options, startContext, debugContext, cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken);
            var lookup = _executableResolver.ResolveProviderExecutable("ClaudeCode", DefaultExecutableCandidates, runtimeEnvironment);
            var executablePath = lookup.ResolvedPath;
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = CliExecutableNotFoundMessage.Describe("Claude Code executable was not found.", "ClaudeCode", lookup)
                };
            }

            var result = await _processManager.ExecuteAsync(
                new ProcessStartContext
                {
                    ExecutablePath = executablePath,
                    Arguments = ["--version"],
                    EnvironmentVariables = runtimeEnvironment,
                    Timeout = TimeSpan.FromSeconds(10),
                    ApplyResourceLimits = false
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

    internal virtual IReadOnlyList<string> BuildCommandArguments(ClaudeCodeOptions options)
    {
        var arguments = new List<string>
        {
            "--output-format",
            "stream-json",
            "--verbose",
            "--input-format",
            "stream-json",
            "--replay-user-messages"
        };

        // Registers AskUserQuestion with the CLI and routes its permission prompts to the host. Without a handler the
        // argument list is exactly what it was before the flag existed.
        if (options.UserInputHandler is not null)
        {
            arguments.AddRange(["--permission-prompt-tool", "stdio"]);
        }

        var model = ArgumentValueNormalizer.NormalizeOptionalValue(options.Model);
        if (model is not null)
        {
            arguments.AddRange(["--model", model]);
        }

        var effort = NormalizeEffort(options.Effort);
        if (effort is not null)
        {
            arguments.AddRange(["--effort", effort]);
        }

        var thinkingDisplay = NormalizeThinkingDisplay(options.ThinkingDisplay);
        if (thinkingDisplay is not null)
        {
            arguments.AddRange([ThinkingDisplayFlag, thinkingDisplay]);
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

        // Sessions are restored by explicit id only. --continue is never emitted: it follows the most recent
        // conversation in the working directory, which can restore the wrong thread.
        var resume = ArgumentValueNormalizer.NormalizeOptionalValue(options.Resume);
        if (resume is not null)
        {
            arguments.AddRange(["--resume", resume]);
        }
        else
        {
            var sessionId = ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId);
            if (sessionId is not null)
            {
                arguments.AddRange(["--session-id", sessionId]);
            }
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

        foreach (var pluginDirectory in options.PluginDirectories)
        {
            var normalizedDirectory = ArgumentValueNormalizer.NormalizeOptionalValue(pluginDirectory);
            if (normalizedDirectory is not null)
            {
                arguments.AddRange(["--plugin-dir", normalizedDirectory]);
            }
        }

        var debugFile = ArgumentValueNormalizer.NormalizeOptionalValue(options.DebugFile);
        if (debugFile is not null)
        {
            arguments.AddRange(["--debug-file", debugFile]);
        }

        foreach (var extraArgument in options.ExtraArgs)
        {
            if (string.Equals(extraArgument.Key, "continue", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "ExtraArgs must not contain 'continue': session restore is managed by ClaudeCodeOptions.SessionId and ClaudeCodeOptions.Resume instead of raw CLI flags.",
                    nameof(options));
            }

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
    /// Logs the argument shape and the environment names of one launch. Diagnostics only read the launch, and a failure
    /// is logged at Debug without affecting the run.
    /// </summary>
    private void LogLaunch(ProcessStartContext startContext)
    {
        try
        {
            _logger.LogInformation(
                "Claude Code launch arguments: {Arguments}",
                ClaudeLaunchDiagnostics.DescribeArguments(startContext.Arguments));
            _logger.LogInformation(
                "Claude Code launch environment: {Environment}",
                ClaudeLaunchDiagnostics.DescribeEnvironment(startContext.EnvironmentVariables ?? new Dictionary<string, string?>()));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not describe the Claude Code launch.");
        }
    }

    /// <summary>
    /// Logs a content-free summary of one message written to the CLI's stdin.
    /// </summary>
    private void LogStdinWrite(int sequence, CliMessage message)
    {
        try
        {
            _logger.LogDebug(
                "Claude Code stdin write {Sequence}: {Summary}",
                sequence,
                ClaudeLaunchDiagnostics.DescribeStdinMessage(message));
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not describe a Claude Code stdin write.");
        }
    }

    /// <summary>
    /// Summarizes the <c>system</c> <c>init</c> message and warns about plugin directories the CLI did not expose. The message
    /// is only read; the caller yields it unchanged, and a failure is logged at Debug.
    /// </summary>
    private void ObserveInitMessage(CliMessage message, ClaudeCodeOptions options)
    {
        try
        {
            if (!string.Equals(message.Type, "system", StringComparison.Ordinal)
                || !IsInitMessage(message.Content)
                || !ClaudeInitSummary.TryParse(message.Content, out var summary))
            {
                return;
            }

            _logger.LogInformation("Claude Code init summary: {Summary}", summary.Describe());

            var pluginDirectories = options.PluginDirectories
                .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
                .OfType<string>()
                .ToArray();
            var findings = ClaudeInitSummary.FindUnexposedPlugins(summary, pluginDirectories);
            foreach (var plugin in findings.RegisteredWithoutSkills)
            {
                _logger.LogWarning(
                    "Claude Code plugin registered but skills not exposed: plugin {PluginName} at {PluginPath} has no skill named {PluginName}:...",
                    plugin.Name,
                    plugin.Path,
                    plugin.Name);
            }

            if (findings.PassedWithoutRegistration.Count > 0)
            {
                var listedPaths = summary.Plugins.Count == 0
                    ? "(none)"
                    : string.Join(", ", summary.Plugins.Select(static plugin => plugin.Path ?? "(no path)"));
                foreach (var directory in findings.PassedWithoutRegistration)
                {
                    _logger.LogWarning(
                        "Claude Code plugin directory passed but no plugin registered: {PluginDirectory}; init listed {ListedPluginPaths}",
                        directory,
                        listedPaths);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not summarize the Claude Code init message.");
        }
    }

    private static bool IsInitMessage(JsonElement content)
    {
        return content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("subtype", out var subtype)
            && subtype.ValueKind == JsonValueKind.String
            && string.Equals(subtype.GetString(), "init", StringComparison.Ordinal);
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

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAsync(
        string prompt,
        ClaudeCodeOptions options,
        ProcessStartContext startContext,
        ClaudeCodeDebugContext debugContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // One attempt per request, with a single exception: a CLI that does not know the hidden --thinking-display flag
        // exits before it reads the prompt or writes any stream output, so no conversation state exists to apply twice.
        // The replacement attempt drops only that flag. Every other failure, and any failure after the first message,
        // is surfaced as-is. A C# iterator cannot yield inside a try block that has a catch, so the attempt is driven
        // by hand and messages are yielded outside the try.
        var canFallBack = HasThinkingDisplayArgument(startContext.Arguments);
        var yieldedMessage = false;

        while (true)
        {
            var rejectedThinkingDisplay = false;
            await using var attempt = ExecuteOneShotAttemptAsync(
                    prompt,
                    options,
                    startContext,
                    debugContext,
                    cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                CliMessage message;
                try
                {
                    if (!await attempt.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    message = attempt.Current;
                }
                catch (InvalidOperationException exception) when (
                    canFallBack && !yieldedMessage && IsThinkingDisplayRejection(exception))
                {
                    rejectedThinkingDisplay = true;
                    break;
                }

                yieldedMessage = true;
                yield return message;
            }

            if (!rejectedThinkingDisplay)
            {
                yield break;
            }

            ExecutablesRejectingThinkingDisplay[startContext.ExecutablePath] = true;
            startContext = startContext with { Arguments = RemoveThinkingDisplayArguments(startContext.Arguments) };
            debugContext = CreateDebugContext(options, startContext);
            canFallBack = false;
        }
    }

    private static bool HasThinkingDisplayArgument(IReadOnlyList<string> arguments)
    {
        return arguments.Any(static argument => string.Equals(argument, ThinkingDisplayFlag, StringComparison.Ordinal));
    }

    private static bool IsThinkingDisplayRejection(InvalidOperationException exception)
    {
        return exception.Message.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
               && exception.Message.Contains(ThinkingDisplayFlag, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes every <c>--thinking-display</c> flag and its value, whether it came from the typed option or from ExtraArgs.
    /// </summary>
    internal static IReadOnlyList<string> RemoveThinkingDisplayArguments(IReadOnlyList<string> arguments)
    {
        var filtered = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], ThinkingDisplayFlag, StringComparison.Ordinal))
            {
                filtered.Add(arguments[index]);
                continue;
            }

            // The value is the next token unless the flag was added as a bare switch.
            if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
            }
        }

        return filtered;
    }

    private async IAsyncEnumerable<CliMessage> ExecuteOneShotAttemptAsync(
        string prompt,
        ClaudeCodeOptions options,
        ProcessStartContext startContext,
        ClaudeCodeDebugContext debugContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LogLaunch(startContext);
        await using var transport = CreateTransport(startContext);
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        // The first "result" is not necessarily the last one: a background subagent keeps running after the turn
        // that launched it, and a prompt can be answered by a turn that is not its own. The tracker decides when the
        // run is really over.
        var completionTracker = new ClaudeStreamCompletionTracker(
            options.BackgroundWorkGracePeriod,
            untimedBackgroundCommandTimeout: options.UntimedBackgroundCommandTimeout);
        var turnDelivery = new ClaudeTurnDelivery();
        var promptMessage = CreatePromptMessage(prompt, options);

        // Every write after connect goes through one gate: the receive loop and the background task of each pending
        // question write concurrently.
        var stdinSequence = 0;
        var writer = new ClaudeStdinWriter(transport, message => LogStdinWrite(++stdinSequence, message));
        using var pendingQuestions = options.UserInputHandler is { } userInputHandler
            ? new ClaudePendingQuestions(userInputHandler, options.QuestionTimeout, writer, completionTracker, cancellationToken)
            : null;
        completionTracker.RecordHostPromptSent(prompt);
        await writer.SendAsync(promptMessage, cancellationToken).ConfigureAwait(false);

        // The handle is attached only for the stream below, so every steer is refused once the run's own finally has
        // closed the tracker and detached it.
        var steering = options.Steering;

        // Each wait the run can idle on is bounded. Giving up cancels the receive loop. A C# iterator cannot yield inside
        // a try block that has a catch, so the stream is driven by hand and messages are yielded outside any try.
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var receiver = transport.ReceiveAsync(receiveCancellation.Token)
            .GetAsyncEnumerator(receiveCancellation.Token);
        Task<bool>? pendingReceive = null;
        var promptResent = false;

        // A snapshot of the agent work is an effect of the CLI message that changed it and is yielded right after that
        // message, so a consumer sees the cause first. When the cause is still held back by the turn delivery, the
        // snapshot waits for the next delivery; only the newest snapshot matters because each one is complete.
        AgentWorkSnapshot? pendingSnapshot = null;

        steering?.Attach((steerId, text, steerCancellation) =>
            SendSteerAsync(completionTracker, writer, options, steerId, text, steerCancellation));

        // try/finally is allowed around a yield, unlike catch. Whichever way the run ends, the questions that still wait are
        // released, and a deny that a cancellation started is given a moment to reach the CLI before the transport goes.
        try
        {
            while (true)
            {
                var wait = completionTracker.CurrentWait;
                pendingReceive ??= receiver.MoveNextAsync().AsTask();
                var hasNext = await WaitForReceiveAsync(
                        pendingReceive,
                        ResolveWaitBound(wait, options, completionTracker),
                        receiveCancellation.Token)
                    .ConfigureAwait(false);

                if (hasNext is null)
                {
                    if (wait == ClaudeStreamCompletionTracker.PendingWait.HostPromptStart && !promptResent)
                    {
                        promptResent = true;
                        await writer.SendAsync(promptMessage, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    receiveCancellation.Cancel();
                    await DrainCancelledReceiveAsync(pendingReceive).ConfigureAwait(false);
                    foreach (var held in turnDelivery.Drain())
                    {
                        yield return EnrichMessageWithDebugMetadata(held, debugContext);
                    }

                    if (pendingSnapshot is not null)
                    {
                        yield return pendingSnapshot.ToCliMessage();
                    }

                    yield break;
                }

                pendingReceive = null;
                if (hasNext is false)
                {
                    foreach (var held in turnDelivery.Drain())
                    {
                        yield return EnrichMessageWithDebugMetadata(held, debugContext);
                    }

                    if (pendingSnapshot is not null)
                    {
                        yield return pendingSnapshot.ToCliMessage();
                    }

                    yield break;
                }

                var message = receiver.Current;
                ObserveInitMessage(message, options);

                // Control-protocol traffic is handled before the tracker and the turn delivery see the message: the echo of
                // a response the host wrote looks like run activity to them, and a request that is answered here is not
                // conversation.
                if (ClaudeControlProtocol.IsControlResponseEcho(message))
                {
                    continue;
                }

                if (ClaudeControlProtocol.TryParseRequest(message, out var controlRequest)
                    && !await RouteControlRequestAsync(controlRequest, message, pendingQuestions, writer, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                var complete = completionTracker.Observe(message);
                var steerEcho = completionTracker.LastMessageWasSteerEcho;
                pendingSnapshot = completionTracker.TakeSnapshot() ?? pendingSnapshot;
                var delivered = false;
                foreach (var deliverable in turnDelivery.Accept(message, completionTracker.HostPromptAwaitingStart, steerEcho))
                {
                    delivered = true;
                    yield return EnrichMessageWithDebugMetadata(deliverable, debugContext);
                }

                if (steering is not null)
                {
                    foreach (var resolution in completionTracker.TakeSteerResolutions())
                    {
                        steering.Report(resolution);
                    }
                }

                if (pendingSnapshot is not null && (delivered || complete))
                {
                    yield return pendingSnapshot.ToCliMessage();
                    pendingSnapshot = null;
                }

                if (complete)
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (pendingQuestions is not null)
            {
                pendingQuestions.CancelOutstanding();
                await pendingQuestions.WaitForOutstandingAsync(QuestionDrainTimeout).ConfigureAwait(false);
            }

            // Closing first means no steer can be accepted after this point; whatever the CLI did not echo is handed back
            // as not consumed, and the handle is detached so a late SteerAsync finds nothing to write to.
            if (steering is not null)
            {
                foreach (var resolution in completionTracker.CloseSteering())
                {
                    steering.Report(resolution);
                }

                steering.Detach();
            }
        }
    }

    /// <summary>
    /// Registers a steer with the run's tracker and writes it to the CLI. The registration comes first, so the CLI's echo
    /// cannot arrive before the steer is known, and a run that has already completed refuses it.
    /// </summary>
    private static async Task<ClaudeSteerResult> SendSteerAsync(
        ClaudeStreamCompletionTracker completionTracker,
        ClaudeStdinWriter writer,
        ClaudeCodeOptions options,
        string steerId,
        string text,
        CancellationToken cancellationToken)
    {
        if (!completionTracker.TryRegisterSteer(steerId, text))
        {
            return ClaudeSteerResult.NotSteerable;
        }

        try
        {
            await writer.SendAsync(CreateSteerMessage(steerId, text, options), cancellationToken).ConfigureAwait(false);
            return ClaudeSteerResult.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completionTracker.UnregisterSteer(steerId);
            throw;
        }
        catch (Exception)
        {
            // The transport is gone, so the CLI cannot take the message. It was never accepted, so nothing is reported for it.
            completionTracker.UnregisterSteer(steerId);
            return ClaudeSteerResult.NotSteerable;
        }
    }

    /// <summary>
    /// Answers or routes one <c>control_request</c> of the CLI.
    /// </summary>
    /// <returns>
    /// <see langword="true" /> when the request is an interactive question that is now pending, so it continues through the
    /// normal path and reaches the caller in stream order; <see langword="false" /> when it was handled here and must not
    /// be passed on.
    /// </returns>
    private static async Task<bool> RouteControlRequestAsync(
        ClaudeControlProtocol.ControlRequest request,
        CliMessage message,
        ClaudePendingQuestions? pendingQuestions,
        ClaudeStdinWriter writer,
        CancellationToken cancellationToken)
    {
        // Any other request subtype is neither answered nor passed on.
        if (!request.IsToolPermission)
        {
            return false;
        }

        if (request.IsUserQuestion
            && pendingQuestions is not null
            && ClaudeUserInputRequest.TryFromCliMessage(message, out var question))
        {
            pendingQuestions.Start(question);
            return true;
        }

        // Every other permission request, and a question nobody can answer, gets a fixed deny so the run never hangs on
        // it. The CLI refuses the tools its permission mode does not approve on its own, so deny keeps today's outcome.
        var denyMessage = request.IsUserQuestion || request.RequiresUserInteraction
            ? ClaudeControlProtocol.QuestionUnavailableMessage
            : ClaudeControlProtocol.FixedDenyMessage;
        await writer.SendAsync(ClaudeControlProtocol.BuildDenyResponse(request.RequestId, denyMessage), cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    private static TimeSpan ResolveWaitBound(
        ClaudeStreamCompletionTracker.PendingWait wait,
        ClaudeCodeOptions options,
        ClaudeStreamCompletionTracker completionTracker)
    {
        return wait switch
        {
            ClaudeStreamCompletionTracker.PendingWait.SubagentBackgroundWork => options.SubagentBackgroundWorkTimeout,
            ClaudeStreamCompletionTracker.PendingWait.MainAgentBackgroundWork => completionTracker.RemainingBackgroundWork,
            ClaudeStreamCompletionTracker.PendingWait.HostPromptStart => options.PromptStartTimeout,
            ClaudeStreamCompletionTracker.PendingWait.NotificationTurn => options.NotificationTurnTimeout,
            ClaudeStreamCompletionTracker.PendingWait.SteerDelivery => options.SteerDeliveryTimeout,

            // The agent is blocked on a person; the question timeout of the pending question is the only bound.
            ClaudeStreamCompletionTracker.PendingWait.UserQuestion => Timeout.InfiniteTimeSpan,
            _ => Timeout.InfiniteTimeSpan
        };
    }

    /// <returns>
    /// <see langword="true" /> or <see langword="false" /> when the receive finished, or <see langword="null" /> when the
    /// bound passed first. In that case the receive keeps running and must be awaited or cancelled by the caller.
    /// </returns>
    private static async Task<bool?> WaitForReceiveAsync(
        Task<bool> pendingReceive,
        TimeSpan bound,
        CancellationToken cancellationToken)
    {
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(bound);
        try
        {
            return await pendingReceive.WaitAsync(deadlineSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadlineSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            await DrainCancelledReceiveAsync(pendingReceive).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DrainCancelledReceiveAsync(Task<bool> pendingReceive)
    {
        try
        {
            await pendingReceive.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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

    /// <summary>
    /// Rejects an image the Claude API cannot take before any process starts. The message names the position and the
    /// media type only, never the bytes.
    /// </summary>
    private static void ValidateImages(IReadOnlyList<ClaudeImageAttachment> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index] ?? throw new ArgumentException($"Image attachment {index + 1} is null.", "options");
            if (!ClaudeImageAttachment.IsSupportedMediaType(image.MediaType))
            {
                throw new ArgumentException(
                    $"Image attachment {index + 1} has unsupported media type '{image.MediaType}'. " +
                    $"Supported types: {string.Join(", ", ClaudeImageAttachment.SupportedMediaTypes)}.",
                    "options");
            }

            if (image.Data.IsEmpty)
            {
                throw new ArgumentException($"Image attachment {index + 1} has no data.", "options");
            }
        }
    }

    private static CliMessage CreatePromptMessage(string prompt, ClaudeCodeOptions options)
    {
        // Without images the content stays the plain prompt string, so the wire line is the same as before.
        object content = options.Images.Count == 0
            ? prompt
            : CreateContentBlocks(prompt, options.Images);

        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = content
            },
            ["session_id"] = options.SessionId
        });

        return new CliMessage("user", payload);
    }

    /// <summary>
    /// Builds a follow-up user message for a running execution. Its <c>uuid</c> is the steer id, which is how the CLI's echo
    /// is matched, and its priority is <c>next</c>, so the CLI injects it at the next tool boundary. Text only.
    /// </summary>
    internal static CliMessage CreateSteerMessage(string steerId, string text, ClaudeCodeOptions options)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = text
            },
            ["session_id"] = options.SessionId,
            ["uuid"] = steerId,
            ["priority"] = "next"
        });

        return new CliMessage("user", payload);
    }

    private static List<Dictionary<string, object?>> CreateContentBlocks(string prompt, IReadOnlyList<ClaudeImageAttachment> images)
    {
        var blocks = new List<Dictionary<string, object?>>(images.Count + 1)
        {
            new() { ["type"] = "text", ["text"] = prompt }
        };

        foreach (var image in images)
        {
            blocks.Add(new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["source"] = new Dictionary<string, object?>
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType.ToLowerInvariant(),
                    ["data"] = Convert.ToBase64String(image.Data.Span)
                }
            });
        }

        return blocks;
    }

    private static ClaudeCodeDebugContext CreateDebugContext(
        ClaudeCodeOptions options,
        ProcessStartContext startContext)
    {
        return new ClaudeCodeDebugContext(
            ResolveRequestedSessionId(options),
            BuildRuntimeFingerprint(startContext));
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
        UpsertString(rootNode, "runtime_fingerprint", debugContext.RuntimeFingerprint);
        UpsertString(rootNode, "event_timestamp", message.EventTimestamp.ToString("O"));

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

    private static string BuildRuntimeFingerprint(ProcessStartContext startContext)
    {
        var serializedEnvironment = startContext.EnvironmentVariables is null
            ? string.Empty
            : string.Join(
                "\n",
                startContext.EnvironmentVariables
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => $"{pair.Key}={pair.Value ?? string.Empty}"));
        var fingerprintMaterial = string.Join(
            "\n",
            new[]
            {
                startContext.ExecutablePath,
                startContext.WorkingDirectory ?? string.Empty,
                string.Join('\u001f', startContext.Arguments),
                serializedEnvironment
            });

        return BuildCompactFingerprint(fingerprintMaterial);
    }

    private static string? ResolveRequestedSessionId(ClaudeCodeOptions options)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(options.SessionId)
               ?? ArgumentValueNormalizer.NormalizeOptionalValue(options.Resume);
    }

    private static string? NormalizeEffort(string? effort)
    {
        var normalizedEffort = ArgumentValueNormalizer.NormalizeOptionalValue(effort);
        if (normalizedEffort is null)
        {
            return null;
        }

        if (!AllowedEffortLevels.Contains(normalizedEffort))
        {
            throw new ArgumentException(
                $"Invalid Claude Code effort level '{normalizedEffort}'. " +
                $"Valid values: {string.Join(", ", AllowedEffortLevels.OrderBy(level => level, StringComparer.Ordinal))}.",
                nameof(effort));
        }

        return normalizedEffort.ToLowerInvariant();
    }

    private static string? NormalizeThinkingDisplay(string? thinkingDisplay)
    {
        var normalizedMode = ArgumentValueNormalizer.NormalizeOptionalValue(thinkingDisplay);
        if (normalizedMode is null)
        {
            return null;
        }

        if (!AllowedThinkingDisplayModes.Contains(normalizedMode))
        {
            throw new ArgumentException(
                $"Invalid Claude Code thinking display mode '{normalizedMode}'. " +
                $"Valid values: {string.Join(", ", AllowedThinkingDisplayModes.OrderBy(mode => mode, StringComparer.Ordinal))}.",
                nameof(thinkingDisplay));
        }

        return normalizedMode.ToLowerInvariant();
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
        string RuntimeFingerprint);
}