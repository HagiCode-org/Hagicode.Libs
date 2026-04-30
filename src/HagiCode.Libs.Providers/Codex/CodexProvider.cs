using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Configuration;
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
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates) is not null;

    /// <inheritdoc />
    public async Task<CodexSessionHandle> CreateSessionAsync(
        CodexSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var executablePath = ResolveExecutablePath(options, runtimeEnvironment)
            ?? throw new FileNotFoundException(
                "Unable to locate the Codex executable. Set CodexSessionOptions.ExecutablePath or ensure 'codex' is on PATH.");

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

        var thread = string.IsNullOrWhiteSpace(options.ThreadId)
            ? client.StartThread(options.ThreadOptions)
            : client.ResumeThread(options.ThreadId.Trim(), options.ThreadOptions);

        return new CodexSessionHandle(client, thread);
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
                    ErrorMessage = "Codex executable was not found. Install Codex or set CodexSessionOptions.ExecutablePath."
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

        return _executableResolver.ResolveFirstAvailablePath(DefaultExecutableCandidates, runtimeEnvironment);
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
