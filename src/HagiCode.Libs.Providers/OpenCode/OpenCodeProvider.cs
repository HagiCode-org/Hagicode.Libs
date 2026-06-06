using System.Runtime.CompilerServices;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Implements OpenCode HTTP runtime/session integration.
/// </summary>
public class OpenCodeProvider : ICliProvider<OpenCodeOptions>
{
    private readonly CliExecutableResolver _executableResolver;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly IOpenCodeStandaloneServerClient _standaloneServerClient;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenCodeProvider" /> class.
    /// </summary>
    public OpenCodeProvider(
        CliExecutableResolver executableResolver,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        IOpenCodeStandaloneServerClient? standaloneServerClient = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _standaloneServerClient = standaloneServerClient ?? new OpenCodeStandaloneServerHost(_executableResolver, _runtimeEnvironmentResolver);
    }

    /// <inheritdoc />
    public string Name => "opencode";

    /// <inheritdoc />
    public bool IsAvailable => _executableResolver.ResolveFirstAvailablePath(["opencode"]) is not null;

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ExecuteAsync(
        OpenCodeOptions options,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var runtime = await _standaloneServerClient.AcquireAsync(ToStandaloneOptions(options), cancellationToken).ConfigureAwait(false);
        var sessionResolution = await ResolveSessionAsync(runtime, options, cancellationToken).ConfigureAwait(false);
        var debugContext = BuildDebugContext(sessionResolution);
        var lifecycleMessage = OpenCodeMessageMapper.CreateSessionLifecycleMessage(
            sessionResolution.SessionId,
            sessionResolution.ResumeMode,
            sessionResolution.RequestedSessionId,
            sessionResolution.RuntimeFingerprint,
            sessionResolution.PoolFingerprint);

        var request = OpenCodeSessionPromptRequest.FromText(prompt, ResolveModelSelection(options.Model));
        var response = await runtime.Client.PromptAsync(sessionResolution.SessionId, request, cancellationToken).ConfigureAwait(false);

        yield return lifecycleMessage;

        var assistantText = response.GetTextContent();
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new InvalidOperationException(response.BuildDiagnosticSummary());
        }

        yield return OpenCodeMessageMapper.CreateAssistantMessage(sessionResolution.SessionId, assistantText, response.MessageId, debugContext);
        yield return OpenCodeMessageMapper.CreateTerminalCompletedMessage(sessionResolution.SessionId, assistantText, response.MessageId, debugContext);
    }

    /// <inheritdoc />
    public async Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var options = new OpenCodeOptions();
        try
        {
            var lifecycle = await _standaloneServerClient.WarmupAsync(ToStandaloneOptions(options), cancellationToken).ConfigureAwait(false);
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = lifecycle.Status == OpenCodeStandaloneServerStatus.Ready,
                Version = lifecycle.Version,
                ErrorMessage = lifecycle.Status == OpenCodeStandaloneServerStatus.Ready ? null : lifecycle.ErrorMessage,
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
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _standaloneServerClient.DisposeAsync().ConfigureAwait(false);
    }

    internal static OpenCodeModelSelection? ResolveModelSelection(string? rawModel)
    {
        if (string.IsNullOrWhiteSpace(rawModel))
        {
            return null;
        }

        var normalized = rawModel.Trim();
        var slashIndex = normalized.IndexOf('/');
        if (slashIndex < 0)
        {
            return new OpenCodeModelSelection
            {
                ProviderId = string.Empty,
                ModelId = normalized,
            };
        }

        if (slashIndex == 0 || slashIndex == normalized.Length - 1)
        {
            throw new InvalidOperationException($"OpenCode model '{normalized}' is invalid. Expected '<provider>/<model>' or '<model>'.");
        }

        return new OpenCodeModelSelection
        {
            ProviderId = normalized[..slashIndex],
            ModelId = normalized[(slashIndex + 1)..],
        };
    }

    private async Task<OpenCodeSessionResolution> ResolveSessionAsync(
        OpenCodeStandaloneServerConnection runtime,
        OpenCodeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);

        var requestedSessionId = string.IsNullOrWhiteSpace(options.SessionId) ? null : options.SessionId.Trim();
        var runtimeFingerprint = runtime.RuntimeKey;
        if (requestedSessionId is not null)
        {
            var sessions = await runtime.Client.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (sessions.Any(session => string.Equals(session.Id, requestedSessionId, StringComparison.Ordinal)))
            {
                return new OpenCodeSessionResolution(
                    requestedSessionId,
                    requestedSessionId,
                    ResumeModeResumed,
                    runtimeFingerprint,
                    BuildPoolFingerprint(requestedSessionId, requestedSessionId));
            }
        }

        var createdSessionId = (await runtime.Client.CreateSessionAsync(options.SessionTitle, cancellationToken).ConfigureAwait(false)).Id;
        return new OpenCodeSessionResolution(
            createdSessionId,
            requestedSessionId,
            string.IsNullOrWhiteSpace(requestedSessionId) ? ResumeModeStarted : ResumeModeRestarted,
            runtimeFingerprint,
            BuildPoolFingerprint(requestedSessionId, createdSessionId));
    }

    private static OpenCodeStandaloneServerOptions ToStandaloneOptions(OpenCodeOptions options)
    {
        return new OpenCodeStandaloneServerOptions
        {
            ExecutablePath = options.ExecutablePath,
            BaseUrl = options.BaseUrl,
            WorkingDirectory = options.WorkingDirectory,
            Workspace = options.Workspace,
            StartupTimeout = options.StartupTimeout,
            RequestTimeout = options.RequestTimeout,
            EnvironmentVariables = options.EnvironmentVariables,
            ExtraArguments = options.ExtraArguments,
        };
    }

    private static OpenCodeMessageDebugContext BuildDebugContext(OpenCodeSessionResolution sessionResolution)
    {
        return new OpenCodeMessageDebugContext(
            sessionResolution.SessionId,
            sessionResolution.RequestedSessionId,
            sessionResolution.RuntimeFingerprint,
            sessionResolution.PoolFingerprint,
            sessionResolution.ResumeMode,
            DateTime.UtcNow);
    }

    private static string BuildPoolFingerprint(string? requestedSessionId, string sessionId)
    {
        return string.IsNullOrWhiteSpace(requestedSessionId)
            ? sessionId
            : requestedSessionId.Trim();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(OpenCodeProvider));
        }
    }

    private const string ResumeModeStarted = "started";
    private const string ResumeModeResumed = "resumed";
    private const string ResumeModeRestarted = "restarted";

    private sealed record OpenCodeSessionResolution(
        string SessionId,
        string? RequestedSessionId,
        string ResumeMode,
        string RuntimeFingerprint,
        string PoolFingerprint);
}
