using System.Text.Json;
using System.Threading.Channels;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Implements a minimal ACP bootstrap and prompt session flow.
/// </summary>
public sealed class AcpSessionClient : IAcpSessionClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly AcpJsonRpcClient _rpcClient;
    private readonly Channel<AcpNotification> _sessionNotifications = Channel.CreateUnbounded<AcpNotification>();
    private readonly CancellationTokenSource _disposeCts = new();
    private JsonElement? _initializeResult;
    private Task? _notificationPumpTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcpSessionClient" /> class.
    /// </summary>
    /// <param name="transport">The raw ACP transport.</param>
    public AcpSessionClient(IAcpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _rpcClient = new AcpJsonRpcClient(transport);
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _rpcClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
        EnsureNotificationPumpStarted();
    }

    /// <inheritdoc />
    public async Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initializeResult is { } cachedInitializeResult)
        {
            return cachedInitializeResult;
        }

        var initializeResult = await _rpcClient.InvokeAsync<JsonElement>(
            "initialize",
            new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new
                    {
                        readTextFile = true,
                        writeTextFile = true
                    }
                },
                clientInfo = new
                {
                    name = "HagiCode.Libs",
                    title = "HagiCode Libs",
                    version = typeof(AcpSessionClient).Assembly.GetName().Version?.ToString() ?? "0.0.0"
                }
            },
            cancellationToken).ConfigureAwait(false);

        _initializeResult = initializeResult;
        return initializeResult;
    }

    /// <inheritdoc />
    public async Task<JsonElement> InvokeBootstrapMethodAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        return await _rpcClient.InvokeAsync<JsonElement>(
            method,
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AcpSessionHandle> StartSessionAsync(
        string workingDirectory,
        string? sessionId,
        string? model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var normalizedSessionId = Process.ArgumentValueNormalizer.NormalizeOptionalValue(sessionId);
        var normalizedModel = Process.ArgumentValueNormalizer.NormalizeOptionalValue(model);
        JsonElement sessionResult;
        string resolvedSessionId;
        var isResumed = normalizedSessionId is not null;
        if (isResumed)
        {
            sessionResult = await _rpcClient.InvokeAsync<JsonElement>(
                "session/load",
                new
                {
                    sessionId = normalizedSessionId,
                    cwd = workingDirectory,
                    mcpServers = Array.Empty<object>()
                },
                cancellationToken).ConfigureAwait(false);
            resolvedSessionId = normalizedSessionId!;
        }
        else
        {
            sessionResult = await _rpcClient.InvokeAsync<JsonElement>(
                "session/new",
                new
                {
                    cwd = workingDirectory,
                    mcpServers = Array.Empty<object>()
                },
                cancellationToken).ConfigureAwait(false);
            resolvedSessionId = ParseSessionId(sessionResult);
        }

        if (normalizedModel is not null)
        {
            await _rpcClient.InvokeAsync<JsonElement>(
                "session/set_model",
                new
                {
                    sessionId = resolvedSessionId,
                    modelId = normalizedModel
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new AcpSessionHandle(resolvedSessionId, isResumed, sessionResult.Clone());
    }

    /// <inheritdoc />
    public async Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = await _rpcClient.InvokeAsync<JsonElement>(
            "session/set_mode",
            new
            {
                sessionId,
                modeId
            },
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var promptResult = await _rpcClient.InvokeAsync<JsonElement>(
            "session/prompt",
            new
            {
                sessionId,
                prompt = new object[]
                {
                    new
                    {
                        type = "text",
                        text = prompt
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        TryEnqueuePromptCompletedNotification(sessionId, promptResult);
        return promptResult;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync(CancellationToken cancellationToken = default)
    {
        return _sessionNotifications.Reader.ReadAllAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();

        Exception? notificationPumpException = null;
        if (_notificationPumpTask is not null)
        {
            try
            {
                await _notificationPumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                notificationPumpException = ex;
            }
        }

        _disposeCts.Dispose();
        await _rpcClient.DisposeAsync().ConfigureAwait(false);

        if (notificationPumpException is not null)
        {
            throw notificationPumpException;
        }
    }

    private void EnsureNotificationPumpStarted()
    {
        _notificationPumpTask ??= Task.Run(() => PumpNotificationsAsync(_disposeCts.Token), CancellationToken.None);
    }

    private async Task PumpNotificationsAsync(CancellationToken cancellationToken)
    {
        Exception? terminalException = null;
        try
        {
            await foreach (var notification in _rpcClient.ReceiveNotificationsAsync(cancellationToken).ConfigureAwait(false))
            {
                await _sessionNotifications.Writer.WriteAsync(notification, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            terminalException = ex;
        }
        finally
        {
            _sessionNotifications.Writer.TryComplete(terminalException);
        }
    }

    private void TryEnqueuePromptCompletedNotification(string sessionId, JsonElement promptResult)
    {
        if (!ShouldEnqueuePromptCompletedNotification(promptResult))
        {
            return;
        }

        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId,
                update = new
                {
                    sessionUpdate = "prompt_completed",
                    stopReason = TryGetString(promptResult, "stopReason") ?? TryGetString(promptResult, "status")
                },
                result = promptResult
            }));

        _sessionNotifications.Writer.TryWrite(notification);
    }

    private static bool ShouldEnqueuePromptCompletedNotification(JsonElement promptResult)
    {
        var stopReason = TryGetString(promptResult, "stopReason") ?? TryGetString(promptResult, "status");
        return string.Equals(stopReason, "end_turn", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "success", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static string ParseSessionId(JsonElement sessionResult)
    {
        if (sessionResult.ValueKind == JsonValueKind.Object)
        {
            return ParseSessionIdFromObject(sessionResult);
        }

        if (sessionResult.ValueKind == JsonValueKind.String)
        {
            return ParseSessionIdFromString(sessionResult.GetString());
        }

        throw new JsonException($"session/new returned unsupported result kind: {sessionResult.ValueKind}.");
    }

    private static string ParseSessionIdFromObject(JsonElement sessionResult)
    {
        if (!sessionResult.TryGetProperty("sessionId", out var sessionIdElement) ||
            sessionIdElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
        {
            throw new JsonException("session/new result did not include a valid sessionId.");
        }

        return sessionIdElement.GetString()!;
    }

    private static string ParseSessionIdFromString(string? rawSessionResult)
    {
        if (string.IsNullOrWhiteSpace(rawSessionResult))
        {
            throw new JsonException("session/new returned an empty string result.");
        }

        foreach (var payload in AcpTransportMessageParser.SplitIncomingPayloads(rawSessionResult))
        {
            var sanitizedPayload = AcpTransportMessageParser.SanitizeIncomingMessage(payload, out _);
            if (string.IsNullOrWhiteSpace(sanitizedPayload))
            {
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(sanitizedPayload, SerializerOptions);
                if (parsed.ValueKind == JsonValueKind.Object)
                {
                    return ParseSessionIdFromObject(parsed);
                }
            }
            catch (JsonException)
            {
            }
        }

        throw new JsonException("session/new returned a string result that did not contain a valid JSON object.");
    }
}
