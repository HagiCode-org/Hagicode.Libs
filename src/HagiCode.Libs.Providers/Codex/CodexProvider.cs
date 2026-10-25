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
        var lookup = ResolveLaunchLookup(options, runtimeEnvironment);
        var executablePath = lookup.ResolvedPath
            ?? throw new FileNotFoundException(CliExecutableNotFoundMessage.Describe(
                "Unable to locate the Codex executable. Set CodexSessionOptions.ExecutablePath or ensure 'codex' is on PATH.",
                "Codex",
                lookup));

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

        var userInputHandler = options.InteractiveUserInput ? options.UserInputHandler : null;
        if (userInputHandler is not null || options.Steering is not null)
        {
            if (userInputHandler is not null)
            {
                ValidateQuestionTimeout(options.QuestionTimeout);
            }

            if (options.Steering is not null)
            {
                ValidateSteerDeliveryTimeout(options.SteerDeliveryTimeout);
            }

            var appServer = await TryOpenAppServerSessionAsync(
                    executablePath,
                    sdkOptions,
                    options,
                    userInputHandler,
                    cancellationToken)
                .ConfigureAwait(false);
            if (appServer is not null)
            {
                if (options.Steering is { } steering)
                {
                    appServer.AttachSteering(steering, options.SteerDeliveryTimeout);
                }

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
    /// Opens the interactive transport for requested questions or steering, or returns <see langword="null"/> so the run uses
    /// the exec transport without those interactive features. The transport is refused below
    /// <see cref="CodexAppServerSession.MinimumCliVersion"/>, when the version cannot be read, or when the handshake fails. The
    /// user-input feature is checked only when an agent question handler is enabled; native steering does not require that feature.
    /// </summary>
    private async Task<CodexAppServerSession?> TryOpenAppServerSessionAsync(
        string executablePath,
        CodexOptions sdkOptions,
        CodexSessionOptions options,
        CodexUserInputHandler? userInputHandler,
        CancellationToken cancellationToken)
    {
        string? installedVersion;
        try
        {
            installedVersion = CodexCliMetadataReader.Read(CodexCliLocator.FindCodexPath(executablePath)).InstalledVersion;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the Codex CLI version; interactive Codex features are unavailable for this run.");
            return null;
        }

        if (!CodexAppServerSession.MeetsMinimumCliVersion(installedVersion))
        {
            _logger.LogWarning(
                "Codex CLI {InstalledVersion} is below the interactive minimum {MinimumVersion}; interactive Codex features are unavailable for this run.",
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
                "The Codex app-server did not start for an interactive run; interactive Codex features are unavailable for this run.");
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

    private static void ValidateSteerDeliveryTimeout(TimeSpan deliveryTimeout)
    {
        if (deliveryTimeout == Timeout.InfiniteTimeSpan || deliveryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deliveryTimeout),
                deliveryTimeout,
                "The steering delivery timeout must be positive and finite.");
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

    /// <summary>
    /// Returns the lookup the launch uses. An explicit path is launched as given when it does not resolve, so the launch fails in the process.
    /// </summary>
    private CliExecutableLookupResult ResolveLaunchLookup(
        CodexSessionOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        var explicitPath = NormalizeOptional(options.ExecutablePath);
        if (explicitPath is null)
        {
            return _executableResolver.ResolveProviderExecutable("Codex", DefaultExecutableCandidates, runtimeEnvironment);
        }

        var resolved = _executableResolver.ResolveExecutablePath(explicitPath, runtimeEnvironment) ?? explicitPath;
        return new CliExecutableLookupResult(resolved, null, []) { AcceptedPaths = [resolved] };
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
