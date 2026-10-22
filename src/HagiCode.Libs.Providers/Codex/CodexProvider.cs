using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Configuration;
using ManagedCode.CodexSharpSDK.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Implements the SDK-backed Codex CLI integration.
/// </summary>
public sealed class CodexProvider : ICodexProvider
{
    private static readonly string[] DefaultExecutableCandidates = ["codex", "codex-cli"];

    private readonly CliExecutableResolver _executableResolver;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly ICliExecutionFacade? _executionFacade;
    private readonly ILogger<CodexProvider> _logger;

    public CodexProvider(
        CliExecutableResolver executableResolver,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        ICliExecutionFacade? executionFacade = null,
        ILogger<CodexProvider>? logger = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _executionFacade = executionFacade;
        _logger = logger ?? NullLogger<CodexProvider>.Instance;
    }

    /// <inheritdoc />
    public string Name => "codex";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveProviderExecutable("Codex", DefaultExecutableCandidates).IsFound;

    /// <inheritdoc />
    public async Task<CodexSessionHandle> CreateSessionAsync(
        CodexSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(CliExecutableNotFoundMessage.Describe(
                "Unable to locate the Codex executable. Set CodexSessionOptions.ExecutablePath or ensure 'codex' is on PATH.",
                "Codex",
                _executableResolver.ResolveProviderExecutable("Codex", DefaultExecutableCandidates, runtimeEnvironment)));

        var sdkOptions = new CodexOptions
        {
            CodexExecutablePath = executablePath,
            ApiKey = NormalizeOptional(options.ApiKey),
            BaseUrl = NormalizeOptional(options.BaseUrl),
            Config = options.Config,
            EnvironmentVariables = BuildEnvironmentVariables(runtimeEnvironment, options.EnvironmentVariables),
            Logger = _logger
        };

        var client = new CodexClient(new CodexClientOptions
        {
            AutoStart = true,
            CodexOptions = sdkOptions
        });

        if (options.InteractiveUserInput && options.UserInputHandler is { } userInputHandler)
        {
            ValidateQuestionTimeout(options.QuestionTimeout);
            var appServer = await TryOpenAppServerSessionAsync(
                    executablePath,
                    sdkOptions,
                    options,
                    userInputHandler,
                    cancellationToken)
                .ConfigureAwait(false);
            if (appServer is not null)
            {
                var appServerThreadId = string.IsNullOrWhiteSpace(options.ThreadId) ? null : options.ThreadId.Trim();
                var appServerThread = client.CreateAppServerThread(appServer, appServerThreadId, options.ThreadOptions);
                return new CodexSessionHandle(client, appServerThread, appServer);
            }
        }

        var thread = string.IsNullOrWhiteSpace(options.ThreadId)
            ? client.StartThread(options.ThreadOptions)
            : client.ResumeThread(options.ThreadId.Trim(), options.ThreadOptions);

        return new CodexSessionHandle(client, thread);
    }

    /// <summary>
    /// Opens the interactive transport, or returns <see langword="null"/> so the run uses the exec transport with questions
    /// disabled (design D7). The transport is refused below <see cref="CodexAppServerSession.MinimumCliVersion"/>, when the
    /// version cannot be read, and when the app-server handshake or the user-input feature check fails.
    /// </summary>
    private async Task<CodexAppServerSession?> TryOpenAppServerSessionAsync(
        string executablePath,
        CodexOptions sdkOptions,
        CodexSessionOptions options,
        CodexUserInputHandler userInputHandler,
        CancellationToken cancellationToken)
    {
        string? installedVersion;
        try
        {
            installedVersion = CodexCliMetadataReader.Read(CodexCliLocator.FindCodexPath(executablePath)).InstalledVersion;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the Codex CLI version; user questions are disabled for this run.");
            return null;
        }

        if (!CodexAppServerSession.MeetsMinimumCliVersion(installedVersion))
        {
            _logger.LogWarning(
                "Codex CLI {InstalledVersion} is below the interactive minimum {MinimumVersion}; user questions are disabled for this run.",
                installedVersion ?? "(unknown)",
                CodexAppServerSession.MinimumCliVersion);
            return null;
        }

        try
        {
            var environment = CodexAppServerSession.BuildEnvironment(
                sdkOptions.EnvironmentVariables,
                sdkOptions.BaseUrl,
                sdkOptions.ApiKey);
            return await CodexAppServerSession.OpenAsync(
                    executablePath,
                    environment,
                    options.ThreadOptions.WorkingDirectory,
                    userInputHandler,
                    options.QuestionTimeout,
                    _logger,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "The Codex app-server did not start for an interactive run; user questions are disabled for this run.");
            return null;
        }
    }

    private static void ValidateQuestionTimeout(TimeSpan questionTimeout)
    {
        if (questionTimeout != Timeout.InfiniteTimeSpan && questionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(questionTimeout),
                questionTimeout,
                "The question timeout must be positive, or Timeout.InfiniteTimeSpan.");
        }
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var lookup = _executableResolver.ResolveProviderExecutable("Codex", DefaultExecutableCandidates, runtimeEnvironment);
            var executablePath = lookup.ResolvedPath;
            if (executablePath is null)
            {
                return new CliProviderTestResult
                {
                    ProviderName = Name,
                    Success = false,
                    ErrorMessage = CliExecutableNotFoundMessage.Describe(
                        "Codex executable was not found. Install Codex or set CodexSessionOptions.ExecutablePath.",
                        "Codex",
                        lookup)
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
                cancellationToken).ConfigureAwait(false);

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
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveExecutablePath(
        CodexSessionOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var explicitPath = NormalizeOptional(options.ExecutablePath);
        if (explicitPath is not null)
        {
            return _executableResolver.ResolveExecutablePath(explicitPath, runtimeEnvironment) ?? explicitPath;
        }

        return _executableResolver.ResolveProviderExecutable("Codex", DefaultExecutableCandidates, runtimeEnvironment).ResolvedPath;
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironmentVariables(
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        IReadOnlyDictionary<string, string?> explicitEnvironmentVariables)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in runtimeEnvironment)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                environment[key] = value;
            }
        }

        foreach (var (key, value) in explicitEnvironmentVariables)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private ICliExecutionFacade ResolveExecutionFacade()
    {
        return _executionFacade ?? new CliExecutionFacade(new CliProcessManager(), _runtimeEnvironmentResolver);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
