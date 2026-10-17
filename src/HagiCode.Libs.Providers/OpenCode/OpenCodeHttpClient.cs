using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Talks to the OpenCode 2.0 <c>/api/*</c> HTTP interface with Basic credentials (username <c>opencode</c>).
/// </summary>
public sealed class OpenCodeHttpClient : IDisposable
{
    internal const string BasicUsername = "opencode";

    private const int BodyExcerptLength = 200;

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string? _directory;

    /// <param name="httpClient">The connection's HTTP client. Basic credentials are set on it when a password is supplied.</param>
    /// <param name="directory">Working directory sent as <c>location.directory</c> when a session is created.</param>
    /// <param name="workspace">Accepted for source compatibility. OpenCode 2.0 has no workspace query, so it is not sent.</param>
    /// <param name="serverPassword">The server password. When <c>null</c> or empty, requests carry no credential.</param>
    public OpenCodeHttpClient(HttpClient httpClient, string? directory, string? workspace, string? serverPassword = null)
    {
        _ = workspace;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

        if (!string.IsNullOrEmpty(serverPassword))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BasicUsername}:{serverPassword}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public Uri BaseUri => _httpClient.BaseAddress ?? new Uri("http://127.0.0.1/");

    public Task<OpenCodeHealthResponse> HealthAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync<OpenCodeHealthResponse>(HttpMethod.Get, BuildUri("/api/info"), null, cancellationToken);
    }

    public Task<IReadOnlyList<OpenCodeProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync<IReadOnlyList<OpenCodeProject>>(HttpMethod.Get, BuildUri("/api/project"), null, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenCodeSession>> ListSessionsAsync(
        string? search = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var page = await SendAsync<OpenCodeSessionPage>(
            HttpMethod.Get,
            BuildUri("/api/session", Query(("search", search), ("limit", limit))),
            null,
            cancellationToken).ConfigureAwait(false);
        return page.Data;
    }

    public Task<OpenCodeSession> CreateSessionAsync(string? title, CancellationToken cancellationToken = default)
    {
        return CreateSessionAsync(
            new OpenCodeSessionCreateRequest { Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() },
            cancellationToken);
    }

    public async Task<OpenCodeSession> CreateSessionAsync(
        OpenCodeSessionCreateRequest? request,
        CancellationToken cancellationToken = default)
    {
        var body = new OpenCodeSessionCreateRequest
        {
            ParentId = request?.ParentId,
            Title = request?.Title,
            Location = request?.Location ?? DirectoryLocation(),
            Permissions = request?.Permissions,
        };

        var response = await SendAsync<OpenCodeDataResponse<OpenCodeSession>>(
            HttpMethod.Post,
            BuildUri("/api/session"),
            body,
            cancellationToken).ConfigureAwait(false);
        return response.Data ?? throw new InvalidOperationException("OpenCode API session creation returned no session.");
    }

    /// <summary>
    /// Sets the model used by a session (<c>POST /api/session/{id}/model</c>). OpenCode 2.0 ignores a model sent on the prompt.
    /// </summary>
    public Task SetSessionModelAsync(
        string sessionId,
        OpenCodeModelSelection model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(model);

        return SendForBodyAsync(
            HttpMethod.Post,
            BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/model"),
            new OpenCodeSessionModelRequest { Model = OpenCodeModelRef.From(model) },
            cancellationToken);
    }

    public async Task<IReadOnlyList<OpenCodeMessageEnvelope>> GetMessagesAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var root = await SendAsync<JsonElement>(
            HttpMethod.Get,
            BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/message", Query(("limit", limit))),
            null,
            cancellationToken).ConfigureAwait(false);

        var messages = new List<OpenCodeMessageEnvelope>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
            var type = item.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            var parts = new List<JsonElement>();
            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(content.EnumerateArray().Select(static part => part.Clone()));
            }
            else if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                parts.Add(JsonSerializer.SerializeToElement(new { type = "text", text = text.GetString() }));
            }

            messages.Add(new OpenCodeMessageEnvelope
            {
                Info = JsonSerializer.SerializeToElement(new { id, sessionID = sessionId, role = type }),
                Parts = parts,
            });
        }

        return messages;
    }

    /// <summary>
    /// Runs one prompt and returns the assistant reply assembled from <c>GET /api/event</c>.
    /// The event stream is opened and confirmed before the prompt is posted, so early events of the turn are not lost.
    /// </summary>
    public async Task<OpenCodeMessageEnvelope> PromptAsync(
        string sessionId,
        OpenCodeSessionPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        using var turnSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestTimeout = _httpClient.Timeout;
        if (requestTimeout != Timeout.InfiniteTimeSpan)
        {
            turnSource.CancelAfter(requestTimeout);
        }

        var turnToken = turnSource.Token;
        try
        {
            await using var events = SubscribeEventsAsync(turnToken).GetAsyncEnumerator(turnToken);
            if (!await events.MoveNextAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("OpenCode event stream closed before the prompt was sent.");
            }

            var ack = await SendAsync<OpenCodeDataResponse<OpenCodeInboxUser>>(
                HttpMethod.Post,
                BuildUri($"/api/session/{Uri.EscapeDataString(sessionId)}/prompt"),
                request,
                turnToken).ConfigureAwait(false);

            return await AwaitTurnAsync(events, sessionId, ack.Data?.Id, turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"OpenCode prompt for session '{sessionId}' did not finish within {requestTimeout}.");
        }
    }

    public async Task<IReadOnlyList<OpenCodeFileNode>> ListFilesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var listing = await SendAsync<OpenCodeFileSystemListResponse>(
            HttpMethod.Get,
            BuildUri("/api/fs/list", Query(("path", path), ("location[directory]", _directory))),
            null,
            cancellationToken).ConfigureAwait(false);

        return listing.Data
            .Select(entry => new OpenCodeFileNode
            {
                Name = Path.GetFileName(entry.Path),
                Path = entry.Path,
                Type = entry.Type,
            })
            .ToArray();
    }

    public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/api/event"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, request.RequestUri!, cancellationToken).ConfigureAwait(false);
        EnsureNotHtml(response, request.RequestUri!, body: null);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        var dataBuilder = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName is not null || dataBuilder.Length > 0)
                {
                    yield return CreateEvent(eventName, dataBuilder.ToString());
                    eventName = null;
                    dataBuilder.Clear();
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                // Comment or heartbeat line.
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.AppendLine();
                }

                dataBuilder.Append(line[5..].TrimStart());
            }
        }

        if (eventName is not null || dataBuilder.Length > 0)
        {
            yield return CreateEvent(eventName, dataBuilder.ToString());
        }
    }

    private async Task<OpenCodeMessageEnvelope> AwaitTurnAsync(
        IAsyncEnumerator<OpenCodeEventEnvelope> events,
        string sessionId,
        string? promptMessageId,
        CancellationToken cancellationToken)
    {
        var textSegments = new List<string>();
        string? assistantMessageId = null;

        while (await events.MoveNextAsync().ConfigureAwait(false))
        {
            var envelope = events.Current;
            if (!string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            switch (envelope.EventType)
            {
                case "session.text.ended":
                    assistantMessageId = TryGetString(envelope.Payload, "assistantMessageID") ?? assistantMessageId;
                    var text = TryGetString(envelope.Payload, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textSegments.Add(text);
                    }

                    break;

                case "session.execution.succeeded":
                    return BuildAssistantEnvelope(
                        sessionId,
                        assistantMessageId ?? promptMessageId,
                        textSegments.Select(static segment => JsonSerializer.SerializeToElement(new { type = "text", text = segment })).ToList());

                case "session.execution.failed":
                    var error = envelope.Payload.ValueKind == JsonValueKind.Object && envelope.Payload.TryGetProperty("error", out var errorNode)
                        ? errorNode
                        : default;
                    var errorPart = JsonSerializer.SerializeToElement(new
                    {
                        type = "error",
                        reason = TryGetString(error, "type") ?? "unknown",
                        message = TryGetString(error, "message") ?? "OpenCode reported a failed turn.",
                    });
                    return BuildAssistantEnvelope(sessionId, assistantMessageId ?? promptMessageId, [errorPart]);
            }
        }

        throw new InvalidOperationException("OpenCode event stream ended before the prompt turn finished.");
    }

    private static OpenCodeMessageEnvelope BuildAssistantEnvelope(string sessionId, string? messageId, List<JsonElement> parts)
    {
        return new OpenCodeMessageEnvelope
        {
            Info = JsonSerializer.SerializeToElement(new
            {
                id = messageId ?? "(unknown)",
                sessionID = sessionId,
                role = "assistant",
            }),
            Parts = parts,
        };
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        Uri requestUri,
        object? body,
        CancellationToken cancellationToken)
    {
        var payload = await SendForBodyAsync(method, requestUri, body, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<T>(payload, _jsonOptions);
        return result ?? throw new InvalidOperationException($"OpenCode API request to '{requestUri}' returned an empty JSON payload.");
    }

    private async Task<string> SendForBodyAsync(
        HttpMethod method,
        Uri requestUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        var payload = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureNotHtml(response, requestUri, payload);
        return payload;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, Uri requestUri, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new OpenCodeAuthenticationException(requestUri.ToString());
        }

        var payload = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new OpenCodeApiException(
            response.StatusCode,
            $"OpenCode API request to '{requestUri}' failed with status {(int)response.StatusCode} ({response.StatusCode}).",
            NormalizeBody(payload));
    }

    /// <summary>
    /// The 1.x and 2.x legacy routes answer with the Web UI HTML shell and HTTP 200, so an HTML body is treated as an unsupported API.
    /// </summary>
    private static void EnsureNotHtml(HttpResponseMessage response, Uri requestUri, string? body)
    {
        var isHtmlContentType = string.Equals(
            response.Content?.Headers.ContentType?.MediaType,
            "text/html",
            StringComparison.OrdinalIgnoreCase);
        var isHtmlBody = body is not null && body.TrimStart().StartsWith('<');
        if (isHtmlContentType || isHtmlBody)
        {
            throw new OpenCodeUnsupportedApiException(requestUri.ToString(), BuildExcerpt(body));
        }
    }

    private static string? BuildExcerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        return trimmed.Length <= BodyExcerptLength ? trimmed : trimmed[..BodyExcerptLength] + "...";
    }

    private static string? NormalizeBody(string? payload)
    {
        return string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
    }

    private OpenCodeLocationRef? DirectoryLocation()
    {
        return _directory is null ? null : new OpenCodeLocationRef { Directory = _directory };
    }

    private static OpenCodeEventEnvelope CreateEvent(string? eventName, string payload)
    {
        JsonElement data;
        if (string.IsNullOrWhiteSpace(payload))
        {
            data = JsonSerializer.SerializeToElement(new { });
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                data = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                data = JsonSerializer.SerializeToElement(payload);
            }
        }

        return new OpenCodeEventEnvelope
        {
            EventName = eventName,
            Data = data,
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static Uri BuildUri(string path, string? query = null)
    {
        var normalized = string.IsNullOrWhiteSpace(query) ? path : $"{path}?{query}";
        return new Uri(normalized, UriKind.Relative);
    }

    private static string? Query(params (string Key, object? Value)[] values)
    {
        var parts = values
            .Where(static entry => entry.Value is not null)
            .Select(static entry =>
                $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? string.Empty)}")
            .ToArray();

        return parts.Length == 0 ? null : string.Join("&", parts);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
