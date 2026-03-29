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
        var lifecycleMessages = new List<CliMessage>
        {
            OpenCodeMessageMapper.CreateSessionLifecycleMessage(
                sessionResolution.SessionId,
                sessionResolution.Resumed,
                sessionResolution.RequestedSessionId)
        };

        var request = OpenCodeSessionPromptRequest.FromText(prompt, ResolveModelSelection(options.Model));
        OpenCodeMessageEnvelope response;
        try
        {
            response = await runtime.Client.PromptAsync(sessionResolution.SessionId, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryWithFreshRuntime(ex))
        {
            await _standaloneServerClient.InvalidateAsync(
                ToStandaloneOptions(options),
                reason: "OpenCode prompt failed and requested a fresh runtime.",
                cancellationToken).ConfigureAwait(false);
            runtime = await _standaloneServerClient.AcquireAsync(ToStandaloneOptions(options), cancellationToken).ConfigureAwait(false);
            sessionResolution = await ResolveSessionAsync(runtime, options with { SessionId = null }, cancellationToken).ConfigureAwait(false);
            lifecycleMessages.Add(
                OpenCodeMessageMapper.CreateSessionLifecycleMessage(
                    sessionResolution.SessionId,
                    false,
                    options.SessionId));
            response = await runtime.Client.PromptAsync(sessionResolution.SessionId, request, cancellationToken).ConfigureAwait(false);
        }

        foreach (var lifecycleMessage in lifecycleMessages)
        {
            yield return lifecycleMessage;
        }

        var assistantText = response.GetTextContent();
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new InvalidOperationException(response.BuildDiagnosticSummary());
        }

        yield return OpenCodeMessageMapper.CreateAssistantMessage(sessionResolution.SessionId, assistantText, response.MessageId);
        yield return OpenCodeMessageMapper.CreateTerminalCompletedMessage(sessionResolution.SessionId, assistantText, response.MessageId);
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
        if (requestedSessionId is not null)
        {
            var sessions = await runtime.Client.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (sessions.Any(session => string.Equals(session.Id, requestedSessionId, StringComparison.Ordinal)))
            {
                return new OpenCodeSessionResolution(requestedSessionId, true, requestedSessionId);
            }
        }

        return new OpenCodeSessionResolution(
            (await CreateSessionWithRecoveryAsync(runtime, options, cancellationToken).ConfigureAwait(false)).Id,
            false,
            requestedSessionId);
    }

    private async Task<OpenCodeSession> CreateSessionWithRecoveryAsync(
        OpenCodeStandaloneServerConnection runtime,
        OpenCodeOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.Client.CreateSessionAsync(options.SessionTitle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryWithFreshRuntime(ex) && runtime.OwnsRuntime)
        {
            await _standaloneServerClient.InvalidateAsync(
                ToStandaloneOptions(options),
                reason: "OpenCode session API became unavailable during session creation.",
                cancellationToken).ConfigureAwait(false);
            var recoveredRuntime = await _standaloneServerClient.AcquireAsync(ToStandaloneOptions(options), cancellationToken).ConfigureAwait(false);
            return await recoveredRuntime.Client.CreateSessionAsync(options.SessionTitle, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetryWithFreshRuntime(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is OpenCodeApiException
            {
                StatusCode: System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.BadGateway
                    or System.Net.HttpStatusCode.ServiceUnavailable
            };
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

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(OpenCodeProvider));
        }
    }

    private sealed record OpenCodeSessionResolution(string SessionId, bool Resumed, string? RequestedSessionId);
}
