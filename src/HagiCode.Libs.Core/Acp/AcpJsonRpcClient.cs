using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Provides minimal JSON-RPC request/response handling for ACP transports.
/// </summary>
public sealed class AcpJsonRpcClient : IAsyncDisposable
{
    private readonly IAcpTransport _transport;
    private readonly Channel<AcpNotification> _notifications = Channel.CreateUnbounded<AcpNotification>();
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _disposeCts = new();

    private Task? _listenTask;
    private long _nextRequestId;
    private bool _connected;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcpJsonRpcClient" /> class.
    /// </summary>
    /// <param name="transport">The transport used for raw message exchange.</param>
    public AcpJsonRpcClient(IAcpTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Opens the underlying transport and starts the receive loop.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_connected)
        {
            throw new InvalidOperationException("The ACP JSON-RPC client is already connected.");
        }

        await _transport.ConnectAsync(cancellationToken);
        _connected = true;
        _listenTask = Task.Run(() => ListenAsync(_disposeCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Invokes one JSON-RPC method and deserializes the result.
    /// </summary>
    /// <typeparam name="T">The expected result type.</typeparam>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">The request parameters.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The deserialized result.</returns>
    public async Task<T?> InvokeAsync<T>(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var requestId = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var pendingRequest = new PendingRequest(method);
        if (!_pendingRequests.TryAdd(requestId, pendingRequest))
        {
            throw new InvalidOperationException($"Failed to register pending ACP request '{requestId}'.");
        }

        try
        {
            await _transport.SendMessageAsync(CreateRequestPayload(requestId, method, parameters), cancellationToken);
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state => ((PendingRequest)state!).TrySetCanceled(),
            pendingRequest);

        JsonElement result;
        try
        {
            result = await pendingRequest.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }

        return DeserializeResult<T>(result);
    }

    /// <summary>
    /// Streams inbound JSON-RPC notifications.
    /// </summary>
    /// <param name="cancellationToken">Cancels notification enumeration.</param>
    /// <returns>The inbound notifications.</returns>
    public IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync(CancellationToken cancellationToken = default)
    {
        return _notifications.Reader.ReadAllAsync(cancellationToken);
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

        Exception? listenException = null;
        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                listenException = ex;
            }
        }

        _disposeCts.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);

        if (listenException is not null)
        {
            throw listenException;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        Exception? terminalException = null;
        var parsedMessageCount = 0;
        var ignoredCommentCount = 0;
        var sawNonCommentPayload = false;
        string? lastIgnoredComment = null;
        string? lastMalformedPayload = null;

        try
        {
            await foreach (var frame in _transport.ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(frame))
                {
                    continue;
                }

                foreach (var payload in AcpTransportMessageParser.SplitIncomingPayloads(frame))
                {
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        continue;
                    }

                    var sanitized = AcpTransportMessageParser.SanitizeIncomingMessage(payload, out var ignoredComment);
                    if (!string.IsNullOrWhiteSpace(ignoredComment))
                    {
                        ignoredCommentCount++;
                        lastIgnoredComment = ignoredComment;
                    }

                    if (string.IsNullOrWhiteSpace(sanitized))
                    {
                        continue;
                    }

                    sawNonCommentPayload = true;

                    try
                    {
                        using var document = JsonDocument.Parse(sanitized);
                        DispatchMessage(document.RootElement.Clone());
                        parsedMessageCount++;
                    }
                    catch (JsonException)
                    {
                        lastMalformedPayload = payload.Length > 200 ? payload[..200] + "..." : payload;
                    }
                }
            }

            if (parsedMessageCount == 0 && sawNonCommentPayload && lastMalformedPayload is not null)
            {
                terminalException = new InvalidOperationException(
                    $"ACP transport ended after malformed payloads before a valid JSON-RPC message was received. Last malformed payload: {lastMalformedPayload}");
            }
            else if (parsedMessageCount == 0 && !sawNonCommentPayload && ignoredCommentCount > 0)
            {
                terminalException = new InvalidOperationException(
                    $"ACP transport ended after only ignorable comment payloads; no valid JSON-RPC message was received. Last ignored comment: {lastIgnoredComment}");
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
            if (terminalException is not null)
            {
                foreach (var pendingRequest in _pendingRequests.Values)
                {
                    pendingRequest.TrySetException(terminalException);
                }
            }

            _notifications.Writer.TryComplete(terminalException);
        }
    }

    private void DispatchMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            if (message.TryGetProperty("id", out var requestIdElement))
            {
                _ = HandleInboundRequestAsync(
                    requestIdElement.Clone(),
                    methodElement.GetString()!,
                    message.TryGetProperty("params", out var requestParamsElement) ? requestParamsElement.Clone() : default,
                    _disposeCts.Token);
                return;
            }

            var parameters = message.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : default;
            _notifications.Writer.TryWrite(new AcpNotification(methodElement.GetString()!, parameters));
            return;
        }

        if (!message.TryGetProperty("id", out var idElement))
        {
            return;
        }

        var requestId = GetRequestId(idElement);
        if (!_pendingRequests.TryGetValue(requestId, out var pendingRequest))
        {
            return;
        }

        if (message.TryGetProperty("error", out var errorElement))
        {
            pendingRequest.TrySetException(CreateRpcException(pendingRequest.Method, errorElement));
            return;
        }

        if (message.TryGetProperty("result", out var resultElement))
        {
            pendingRequest.TrySetResult(resultElement.Clone());
            return;
        }

        pendingRequest.TrySetException(new InvalidOperationException(
            $"ACP response for method '{pendingRequest.Method}' did not contain a result or error payload."));
    }

    private Exception CreateRpcException(string method, JsonElement errorElement)
    {
        var message = errorElement.TryGetProperty("message", out var messageElement) &&
                      messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : null;
        var code = errorElement.TryGetProperty("code", out var codeElement) &&
                   codeElement.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? codeElement.ToString()
            : null;

        var errorMessage =
            $"ACP method '{method}' failed{(string.IsNullOrWhiteSpace(code) ? string.Empty : $" with code {code}")}: {message ?? "Unknown error."}";
        var diagnostics = (_transport as IAcpTransportDiagnosticsSource)?.GetDiagnosticSummary();
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            errorMessage = $"{errorMessage}{System.Environment.NewLine}ACP transport diagnostics: {diagnostics}";
        }

        return new InvalidOperationException(errorMessage);
    }

    private async Task HandleInboundRequestAsync(
        JsonElement requestIdElement,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(method, "session/request_permission", StringComparison.OrdinalIgnoreCase))
            {
                _notifications.Writer.TryWrite(new AcpNotification(method, parameters.Clone()));
            }

            var result = method switch
            {
                "fs/read_text_file" => CreateReadTextFileResponse(parameters),
                "fs/write_text_file" => await CreateWriteTextFileResponseAsync(parameters, cancellationToken).ConfigureAwait(false),
                "session/request_permission" => CreateRequestPermissionResponse(parameters),
                _ => throw new InvalidOperationException($"ACP inbound method '{method}' is not supported.")
            };

            await _transport.SendMessageAsync(
                CreateResponsePayload(requestIdElement, result),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _transport.SendMessageAsync(
                CreateErrorPayload(requestIdElement, -32601, ex.Message),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonElement CreateReadTextFileResponse(JsonElement parameters)
    {
        var path = GetRequiredString(parameters, "path");
        var startLine = GetOptionalInt(parameters, "line") ?? 1;
        var limit = GetOptionalInt(parameters, "limit");

        var allLines = File.ReadAllLines(path);
        var startIndex = Math.Max(startLine - 1, 0);
        var selectedLines = limit is > 0
            ? allLines.Skip(startIndex).Take(limit.Value)
            : allLines.Skip(startIndex);

        return JsonSerializer.SerializeToElement(new
        {
            content = string.Join(global::System.Environment.NewLine, selectedLines)
        });
    }

    private static async Task<JsonElement> CreateWriteTextFileResponseAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var path = GetRequiredString(parameters, "path");
        var content = GetRequiredString(parameters, "content");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { });
    }

    private static JsonElement CreateRequestPermissionResponse(JsonElement parameters)
    {
        var selectedOptionId = ResolvePermissionOptionId(parameters);
        return JsonSerializer.SerializeToElement(new
        {
            outcome = new
            {
                outcome = "selected",
                optionId = selectedOptionId
            }
        });
    }

    private static string ResolvePermissionOptionId(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("options", out var optionsElement) ||
            optionsElement.ValueKind != JsonValueKind.Array)
        {
            return "allow";
        }

        string? allowOnceOptionId = null;
        string? firstOptionId = null;
        foreach (var option in optionsElement.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var optionId = TryGetOptionalString(option, "optionId");
            if (string.IsNullOrWhiteSpace(optionId))
            {
                continue;
            }

            firstOptionId ??= optionId;
            var kind = TryGetOptionalString(option, "kind");
            if (string.Equals(kind, "allow_always", StringComparison.OrdinalIgnoreCase))
            {
                return optionId;
            }

            if (allowOnceOptionId is null &&
                string.Equals(kind, "allow_once", StringComparison.OrdinalIgnoreCase))
            {
                allowOnceOptionId = optionId;
            }
        }

        return allowOnceOptionId ?? firstOptionId ?? "allow";
    }

    private static string? TryGetOptionalString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static string GetRequiredString(JsonElement parameters, string propertyName)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"ACP inbound request is missing required string parameter '{propertyName}'.");
        }

        return propertyElement.GetString() ?? string.Empty;
    }

    private static int? GetOptionalInt(JsonElement parameters, string propertyName)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.Number ||
            !propertyElement.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private string CreateRequestPayload(string requestId, string method, object? parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("id", requestId);
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, _serializerOptions);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CreateResponsePayload(JsonElement requestIdElement, JsonElement result)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + requestIdElement.GetRawText() + ",\"result\":" + result.GetRawText() + "}";
    }

    private static string CreateErrorPayload(JsonElement requestIdElement, int code, string message)
    {
        var error = JsonSerializer.SerializeToElement(new
        {
            code,
            message
        });

        return "{\"jsonrpc\":\"2.0\",\"id\":" + requestIdElement.GetRawText() + ",\"error\":" + error.GetRawText() + "}";
    }

    private static string GetRequestId(JsonElement idElement)
    {
        return idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : idElement.GetRawText();
    }

    private T? DeserializeResult<T>(JsonElement result)
    {
        if (typeof(T) == typeof(JsonElement))
        {
            return (T)(object)result;
        }

        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(result.GetRawText(), _serializerOptions);
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("The ACP JSON-RPC client is not connected.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<JsonElement> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(string method)
        {
            Method = method;
        }

        public string Method { get; }

        public Task<JsonElement> Task => _completionSource.Task;

        public void TrySetResult(JsonElement result) => _completionSource.TrySetResult(result);

        public void TrySetCanceled() => _completionSource.TrySetCanceled();

        public void TrySetException(Exception exception) => _completionSource.TrySetException(exception);
    }
}
