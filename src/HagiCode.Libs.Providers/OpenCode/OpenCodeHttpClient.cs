using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HagiCode.Libs.Providers.OpenCode;

public sealed class OpenCodeHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string? _directory;
    private readonly string? _workspace;

    public OpenCodeHttpClient(HttpClient httpClient, string? directory, string? workspace)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        _workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim();
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
    }

    public Uri BaseUri => _httpClient.BaseAddress ?? new Uri("http://127.0.0.1/");

    public Task<OpenCodeHealthResponse> HealthAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync<OpenCodeHealthResponse>(HttpMethod.Get, BuildUri("/global/health"), null, cancellationToken);
    }

    public Task<IReadOnlyList<OpenCodeProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync<IReadOnlyList<OpenCodeProject>>(
            HttpMethod.Get,
            BuildUri("/project", Query(("directory", _directory), ("workspace", _workspace))),
            null,
            cancellationToken);
    }

    public Task<IReadOnlyList<OpenCodeSession>> ListSessionsAsync(
        bool? roots = null,
        long? start = null,
        string? search = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<IReadOnlyList<OpenCodeSession>>(
            HttpMethod.Get,
            BuildUri(
                "/session",
                Query(
                    ("directory", _directory),
                    ("workspace", _workspace),
                    ("roots", roots),
                    ("start", start),
                    ("search", search),
                    ("limit", limit))),
            null,
            cancellationToken);
    }

    public Task<OpenCodeSession> CreateSessionAsync(string? title, CancellationToken cancellationToken = default)
    {
        return CreateSessionAsync(
            new OpenCodeSessionCreateRequest { Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() },
            cancellationToken);
    }

    public Task<OpenCodeSession> CreateSessionAsync(
        OpenCodeSessionCreateRequest? request,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<OpenCodeSession>(
            HttpMethod.Post,
            BuildUri("/session", Query(("directory", _directory), ("workspace", _workspace))),
            request,
            cancellationToken);
    }

    public Task<IReadOnlyList<OpenCodeMessageEnvelope>> GetMessagesAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return SendAsync<IReadOnlyList<OpenCodeMessageEnvelope>>(
            HttpMethod.Get,
            BuildUri(
                $"/session/{Uri.EscapeDataString(sessionId)}/message",
                Query(("directory", _directory), ("workspace", _workspace), ("limit", limit))),
            null,
            cancellationToken);
    }

    public Task<OpenCodeMessageEnvelope> PromptAsync(
        string sessionId,
        OpenCodeSessionPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync<OpenCodeMessageEnvelope>(
            HttpMethod.Post,
            BuildUri(
                $"/session/{Uri.EscapeDataString(sessionId)}/message",
                Query(("directory", _directory), ("workspace", _workspace))),
            request,
            cancellationToken);
    }

    public Task<IReadOnlyList<OpenCodeFileNode>> ListFilesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return SendAsync<IReadOnlyList<OpenCodeFileNode>>(
            HttpMethod.Get,
            BuildUri(
                "/file",
                Query(("path", path), ("directory", _directory), ("workspace", _workspace))),
            null,
            cancellationToken);
    }

    public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri("/event", Query(("directory", _directory), ("workspace", _workspace))));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        var dataBuilder = new StringBuilder();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

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

    private async Task<T> SendAsync<T>(
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
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<T>(payload, _jsonOptions);
        return result ?? throw new InvalidOperationException($"OpenCode API request to '{requestUri}' returned an empty JSON payload.");
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new OpenCodeApiException(
            response.StatusCode,
            $"OpenCode API request to '{response.RequestMessage?.RequestUri?.ToString() ?? response.RequestMessage?.Method.Method ?? "(unknown)"}' failed with status {(int)response.StatusCode} ({response.StatusCode}).",
            NormalizeBody(payload));
    }

    private static string? NormalizeBody(string? payload)
    {
        return string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
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
