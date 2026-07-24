using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Omp;

/// <summary>
/// Implements OMP CLI integration using line-delimited JSON print mode.
/// </summary>
public class OmpProvider : ICliProvider<OmpOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["omp", "omp-cli"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The set of thinking levels accepted by the OMP <c>--thinking</c> flag.
    /// Values are compared case-insensitively.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedThinkingLevels = new HashSet<string>(
        ["off", "minimal", "low", "medium", "high", "xhigh", "max", "auto"],
        StringComparer.OrdinalIgnoreCase);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly OmpJsonEventMapper _eventMapper = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OmpProvider" /> class.
    /// </summary>
    public OmpProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "omp";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        OmpOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        ProcessStartContext? startContext = null;
        string? startupFailure = null;

        ValidateThinkingLevel(options.Thinking);

        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
                                 ?? throw new FileNotFoundException("Unable to locate the OMP executable.");

            startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = BuildCommandArguments(options, prompt),
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
            var executablePath = _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = "OMP executable was not found."
                };
            }

            var result = await _processManager.ExecuteAsync(
                new ProcessStartContext
                {
                    ExecutablePath = executablePath,
                    Arguments = ["--version"],
                    EnvironmentVariables = runtimeEnvironment,
                    Timeout = TimeSpan.FromSeconds(10),
                    OutputEncoding = Utf8NoBom
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

    internal virtual IReadOnlyList<string> BuildCommandArguments(OmpOptions options, string prompt)
    {
        var arguments = new List<string>
        {
            "--mode",
            "json",
            "--print"
        };

        AddOption(arguments, "--provider", options.Provider);
        AddOption(arguments, "--model", options.Model);
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
            AddOption(arguments, "--resume", options.SessionId);
            AddOption(arguments, "--session-dir", options.SessionDirectory);
        }

        if (options.DisableAllTools)
        {
            arguments.Add("--no-tools");
        }
        else
        {
            // OMP exposes --tools allowlist only (no --no-builtin-tools / --exclude-tools).
            AddJoinedOption(arguments, "--tools", options.AllowedTools);
        }

        foreach (var extraArgument in options.ExtraArguments)
        {
            var normalizedArgument = ArgumentValueNormalizer.NormalizeOptionalValue(extraArgument);
            if (normalizedArgument is not null)
            {
                arguments.Add(normalizedArgument);
            }
        }

        arguments.Add(prompt);
        return arguments;
    }

    internal virtual IReadOnlyDictionary<string, string?> BuildEnvironmentVariables(
        OmpOptions options,
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
        var eventState = _eventMapper.CreateStreamingState(requestedSessionId);
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
                $"Invalid OMP thinking level '{normalizedThinking}'. " +
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

    private string? ResolveExecutablePath(OmpOptions options, IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var explicitExecutable = ArgumentValueNormalizer.NormalizeOptionalValue(options.ExecutablePath);
        if (explicitExecutable is not null)
        {
            return _executableResolver.ResolveExecutablePath(explicitExecutable, runtimeEnvironment);
        }

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
    }

    private static string? ResolveWorkingDirectory(OmpOptions options)
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
