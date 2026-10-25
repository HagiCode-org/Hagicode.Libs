using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Omp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// Implements Pi CLI integration using line-delimited JSON print mode. A request that carries a
/// <see cref="PiOptions.UserInputHandler" /> runs in Pi's RPC mode instead, so the agent can ask the user questions.
/// </summary>
public class PiProvider : ICliProvider<PiOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["pi", "pi-cli"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The set of thinking levels accepted by the Pi <c>--thinking</c> flag.
    /// Values are compared case-insensitively.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedThinkingLevels = new HashSet<string>(
        ["off", "minimal", "low", "medium", "high", "xhigh", "max"],
        StringComparer.OrdinalIgnoreCase);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly PiJsonEventMapper _eventMapper = new();
    private readonly ILogger<PiProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PiProvider" /> class.
    /// </summary>
    public PiProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        ILogger<PiProvider>? logger = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _logger = logger ?? NullLogger<PiProvider>.Instance;
    }

    /// <summary>
    /// How long Pi gets to exit after its standard input was closed (at the end of an RPC run, or after an abort) before the
    /// process is stopped.
    /// </summary>
    internal TimeSpan RpcExitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public string Name => "pi";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveProviderExecutable("Pi", DefaultExecutableCandidates).IsFound;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        PiOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        ProcessStartContext? startContext = null;
        string? startupFailure = null;

        ValidateThinkingLevel(options.Thinking);

        // A handler switches the run to RPC mode; without one (or with every tool disabled) nothing below differs from the
        // JSON print path.
        var usesRpc = UsesRpc(options);
        if (options.UserInputHandler is not null)
        {
            ValidateQuestionTimeout(options);
        }

        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
                                 ?? throw new FileNotFoundException("Unable to locate the Pi executable.");

            startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = usesRpc
                    ? BuildRpcCommandArguments(options, PiQuestionExtension.EnsureExtracted())
                    : BuildCommandArguments(options, prompt),
                WorkingDirectory = ResolveWorkingDirectory(options),
                EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
                InputEncoding = Utf8NoBom,
                OutputEncoding = Utf8NoBom,
                Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
            };

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            startupFailure = ex.Message;
        }

        if (startupFailure is not null)
        {
            yield return CreateProcessFailureMessage(startupFailure);
            yield break;
        }

        if (usesRpc)
        {
            var outcome = new PiRpcOutcome();
            await foreach (var message in ExecuteRpcProcessAsync(startContext!, options, prompt, outcome, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            if (outcome.QuestionToolConflict is null)
            {
                yield break;
            }

            // Pi refused to start because a user extension registers ask_user_question as well, so no question can be asked
            // this run. Nothing was streamed yet; run the request the way it runs without a handler.
            _logger.LogWarning(
                "Pi refused to start with the bundled {ToolName} extension because another extension registers the same tool. " +
                "Running the request in JSON print mode without questions. Pi said: {PiOutput}",
                PiUserInputRequest.QuestionToolName,
                outcome.QuestionToolConflict);

            startContext = startContext! with { Arguments = BuildCommandArguments(options, prompt) };
        }

        await foreach (var message in ExecuteProcessAsync(startContext!, options.SessionId, cancellationToken).ConfigureAwait(false))
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
            var executablePath = _executableResolver.ResolveProviderExecutable("Pi", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "Pi executable was not found."
                };
            }

            var result = await _processManager.ExecuteAsync(
                new ProcessStartContext
                {
                    ExecutablePath = executablePath,
                    Arguments = ["--version"],
                    EnvironmentVariables = runtimeEnvironment,
                    Timeout = TimeSpan.FromSeconds(10),
                    OutputEncoding = Utf8NoBom,
                    ApplyResourceLimits = false
                },
                cancellationToken).ConfigureAwait(false);

            var versionText = SelectFirstNonEmpty(result.StandardOutput, result.StandardError);
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = result.ExitCode == 0,
                Version = result.ExitCode == 0 ? versionText : null,
                ErrorMessage = result.ExitCode == 0 ? null : versionText
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

    internal virtual IReadOnlyList<string> BuildCommandArguments(PiOptions options, string prompt)
    {
        var arguments = new List<string>
        {
            "--mode",
            "json",
            "--print"
        };

        AddOptionArguments(arguments, options, questionExtensionPath: null);
        arguments.Add(prompt);
        return arguments;
    }

    /// <summary>
    /// Builds the command line of an RPC run: the same options as the JSON print command, the bundled question extension
    /// loaded with <c>-e</c>, the question tool added to a tool allowlist, and no prompt (it is sent as an RPC command).
    /// </summary>
    internal virtual IReadOnlyList<string> BuildRpcCommandArguments(PiOptions options, string questionExtensionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionExtensionPath);

        var arguments = new List<string>
        {
            "--mode",
            "rpc"
        };

        AddOptionArguments(arguments, options, questionExtensionPath);
        return arguments;
    }

    /// <summary>
    /// Whether a request runs in RPC mode: it can be asked questions (a handler is set) and Pi has tools to ask them with.
    /// </summary>
    internal static bool UsesRpc(PiOptions options)
    {
        return options.UserInputHandler is not null && !options.DisableAllTools;
    }

    private static void AddOptionArguments(List<string> arguments, PiOptions options, string? questionExtensionPath)
    {
        // Pi 1.0.0 and later refuse "--provider" without "--model" (exit code 1). Fail before launching instead,
        // for every provider: omniroute without a model would otherwise be launched as "--model omniroute/".
        var configuredProvider = ArgumentValueNormalizer.NormalizeOptionalValue(options.Provider);
        if (configuredProvider is not null && ArgumentValueNormalizer.NormalizeOptionalValue(options.Model) is null)
        {
            throw new InvalidOperationException(
                $"Pi provider '{configuredProvider}' is configured without a model. " +
                "A model is required when a provider is set; configure a model or clear the provider.");
        }

        var normalizedModel = OmpProvider.NormalizeModelName(options.Model);
        var isOmniRouteSelector = normalizedModel.StartsWith("omniroute/", StringComparison.OrdinalIgnoreCase);
        var providerIsOmniRoute = string.Equals(options.Provider, "omniroute", StringComparison.OrdinalIgnoreCase);

        if (isOmniRouteSelector || providerIsOmniRoute)
        {
            var selector = isOmniRouteSelector ? normalizedModel : $"omniroute/{normalizedModel}";
            AddOption(arguments, "--model", selector);
        }
        else
        {
            AddOption(arguments, "--provider", options.Provider);
            AddOption(arguments, "--model", normalizedModel);
        }
        AddOption(arguments, "--system-prompt", options.SystemPrompt);

        foreach (var appendSystemPrompt in options.AppendSystemPrompts)
        {
            AddOption(arguments, "--append-system-prompt", appendSystemPrompt);
        }

        AddOption(arguments, "--thinking", options.Thinking);

        if (options.NoSession)
        {
            arguments.Add("--no-session");
        }
        else
        {
            AddOption(arguments, "--session-id", options.SessionId);
            AddOption(arguments, "--session-dir", options.SessionDirectory);
        }

        if (options.DisableAllTools)
        {
            arguments.Add("--no-tools");
        }
        else
        {
            if (options.DisableBuiltinTools)
            {
                arguments.Add("--no-builtin-tools");
            }

            AddJoinedOption(arguments, "--tools", questionExtensionPath is null ? options.AllowedTools : WithQuestionTool(options.AllowedTools));
            AddJoinedOption(arguments, "--exclude-tools", options.ExcludedTools);
        }

        foreach (var skillPath in options.SkillPaths)
        {
            AddOption(arguments, "--skill", skillPath);
        }

        if (questionExtensionPath is not null)
        {
            arguments.Add("-e");
            arguments.Add(questionExtensionPath);
        }

        foreach (var extraArgument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(extraArgument);
            if (normalizedArgument is not null)
            {
                arguments.Add(normalizedArgument);
            }
        }
    }

    /// <summary>
    /// Adds the question tool to a non-empty allowlist, so restricting the tools does not hide the question tool. An empty
    /// allowlist means every tool is active and stays as it is.
    /// </summary>
    private static IReadOnlyList<string> WithQuestionTool(IReadOnlyList<string> allowedTools)
    {
        var configured = allowedTools
            .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
            .Where(static value => value is not null)
            .Cast<string>()
            .ToArray();

        return configured.Length == 0 || configured.Contains(PiUserInputRequest.QuestionToolName, StringComparer.Ordinal)
            ? allowedTools
            : [.. configured, PiUserInputRequest.QuestionToolName];
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        PiOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var environment = new Dictionary<string, string?>(runtimeEnvironment, StringComparer.Ordinal);

        foreach (var pair in options.EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            environment[pair.Key.Trim()] = pair.Value;
        }

        return environment;
    }

    internal virtual async IAsyncEnumerable<CliMessage> ExecuteProcessAsync(
        ProcessStartContext startContext,
        string? requestedSessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Recorded right before the process starts: a session header created after this moment was created by this run.
        var processStartedAtUtc = DateTimeOffset.UtcNow;
        var eventState = _eventMapper.CreateStreamingState(requestedSessionId, processStartedAtUtc);
        await using var handle = await _processManager.StartAsync(startContext, cancellationToken).ConfigureAwait(false);

        TryCloseInput(handle.StandardInput);
        var standardErrorTask = ReadToEndAsync(handle.StandardError, cancellationToken);
        ExceptionDispatchInfo? pendingException = null;
        IReadOnlyList<CliMessage> terminalMessages = [];

        while (true)
        {
            string? line;
            try
            {
                line = await handle.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pendingException = ExceptionDispatchInfo.Capture(ex);
                break;
            }

            if (line is null)
            {
                break;
            }

            foreach (var message in eventState.ProcessOutputLine(line))
            {
                yield return message;
            }
        }

        if (pendingException is null)
        {
            try
            {
                await handle.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var standardError = await standardErrorTask.ConfigureAwait(false);
                terminalMessages = eventState.Complete(handle.Process.ExitCode, standardError);
            }
            catch (Exception ex)
            {
                pendingException = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (pendingException is not null)
        {
            await _processManager.StopAsync(handle, CancellationToken.None).ConfigureAwait(false);
            pendingException.Throw();
        }

        foreach (var message in terminalMessages)
        {
            yield return message;
        }
    }

    /// <summary>
    /// Runs one request through Pi's RPC mode (see <see cref="PiRpcSession" />). The process never outlives the call.
    /// </summary>
    internal virtual async IAsyncEnumerable<CliMessage> ExecuteRpcProcessAsync(
        ProcessStartContext startContext,
        PiOptions options,
        string prompt,
        PiRpcOutcome outcome,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var process = await StartRpcProcessAsync(startContext, cancellationToken).ConfigureAwait(false);

        var settings = new PiRpcSessionSettings(
            options.SessionId,
            ResolveWorkingDirectory(options),
            options.UserInputHandler!,
            options.QuestionTimeout,
            RpcExitTimeout);
        var session = new PiRpcSession(process, _eventMapper.CreateStreamingState(options.SessionId), settings, outcome);

        await foreach (var message in session.RunAsync(prompt, cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    internal virtual async ValueTask<IPiRpcProcess> StartRpcProcessAsync(
        ProcessStartContext startContext,
        CancellationToken cancellationToken)
    {
        var handle = await _processManager.StartAsync(startContext, cancellationToken).ConfigureAwait(false);
        return new PiRpcProcessHandle(_processManager, handle);
    }

    private static void ValidateQuestionTimeout(PiOptions options)
    {
        if (options.QuestionTimeout != Timeout.InfiniteTimeSpan && options.QuestionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.QuestionTimeout,
                "QuestionTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }

    private static void AddOption(List<string> arguments, string optionName, string? value)
    {
        var normalizedValue = ArgumentValueNormalizer.NormalizeOptionalValue(value);
        if (normalizedValue is null)
        {
            return;
        }

        arguments.Add(optionName);
        arguments.Add(normalizedValue);
    }

    private static void AddJoinedOption(List<string> arguments, string optionName, IReadOnlyList<string> values)
    {
        var normalizedValues = values
            .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
            .Where(static value => value is not null)
            .Cast<string>()
            .ToArray();

        if (normalizedValues.Length == 0)
        {
            return;
        }

        arguments.Add(optionName);
        arguments.Add(string.Join(',', normalizedValues));
    }

    private static void ValidateThinkingLevel(string? thinking)
    {
        var normalizedThinking = ArgumentValueNormalizer.NormalizeOptionalValue(thinking);
        if (normalizedThinking is null)
        {
            return;
        }

        if (!AllowedThinkingLevels.Contains(normalizedThinking))
        {
            throw new ArgumentException(
                $"Invalid Pi thinking level '{normalizedThinking}'. " +
                $"Valid values: {string.Join(", ", AllowedThinkingLevels.OrderBy(level => level, StringComparer.Ordinal))}.");
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>();
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveExecutablePath(PiOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var explicitExecutable = ArgumentValueNormalizer.NormalizeOptionalValue(options.ExecutablePath);
        if (explicitExecutable is not null)
        {
            return _executableResolver.ResolveExecutablePath(explicitExecutable, runtimeEnvironment);
        }

        return _executableResolver.ResolveProviderExecutable("Pi", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
    }

    private static string? ResolveWorkingDirectory(PiOptions options)
    {
        return ArgumentValueNormalizer.NormalizeOptionalValue(options.WorkingDirectory);
    }

    private static string SelectFirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalizedValue = ArgumentValueNormalizer.NormalizeOptionalValue(value);
            if (normalizedValue is not null)
            {
                return normalizedValue;
            }
        }

        return string.Empty;
    }

    private static void TryCloseInput(StreamWriter writer)
    {
        try
        {
            writer.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task<string> ReadToEndAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static CliMessage CreateProcessFailureMessage(string diagnosticText)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.failed",
            ["text"] = diagnosticText,
            ["error"] = diagnosticText,
            ["message"] = diagnosticText
        };

        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }
}
