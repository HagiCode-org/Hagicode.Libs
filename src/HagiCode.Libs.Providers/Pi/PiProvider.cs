using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// Implements Pi CLI integration using one-shot JSON print mode.
/// </summary>
public class PiProvider : ICliProvider<PiOptions>
{
    private static readonly string[] DefaultExecutableCandidates = ["pi", "pi-cli"];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliProcessManager _processManager;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly PiJsonEventMapper _eventMapper = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PiProvider" /> class.
    /// </summary>
    public PiProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
    }

    /// <inheritdoc />
    public string Name => "pi";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        PiOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CliMessage> messages;
        try
        {
            var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
                                 ?? throw new FileNotFoundException("Unable to locate the Pi executable.");

            var startContext = new ProcessStartContext
            {
                ExecutablePath = executablePath,
                Arguments = BuildCommandArguments(options, prompt),
                WorkingDirectory = ResolveWorkingDirectory(options),
                EnvironmentVariables = BuildEnvironmentVariables(options, runtimeEnvironment),
                InputEncoding = Utf8NoBom,
                OutputEncoding = Utf8NoBom,
                Ownership = new CliProcessOwnershipRegistration { ProviderName = Name }
            };

            var result = await _processManager.ExecuteAsync(startContext, cancellationToken).ConfigureAwait(false);
            messages = _eventMapper.Normalize(result, options.SessionId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            messages = [CreateProcessFailureMessage(ex.Message)];
        }

        foreach (var message in messages)
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

    internal virtual IReadOnlyList<string> BuildCommandArguments(PiOptions options, string prompt)
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

            AddJoinedOption(arguments, "--tools", options.AllowedTools);
            AddJoinedOption(arguments, "--exclude-tools", options.ExcludedTools);
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

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
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
