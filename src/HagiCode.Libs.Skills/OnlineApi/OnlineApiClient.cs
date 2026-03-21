using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HagiCode.Libs.Skills.OnlineApi.Models;
using HagiCode.Libs.Skills.OnlineApi.Providers;
using Microsoft.Extensions.Options;

namespace HagiCode.Libs.Skills.OnlineApi;

public sealed class OnlineApiClient : IOnlineApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IOnlineApiEndpointProvider _endpointProvider;
    private readonly OnlineApiOptions _options;

    public OnlineApiClient(
        HttpClient httpClient,
        IOnlineApiEndpointProvider endpointProvider,
        IOptions<OnlineApiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpointProvider);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _endpointProvider = endpointProvider;
        _options = options.Value;
    }

    public async Task<SearchSkillsResponse> SearchAsync(SearchSkillsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Search query is required.", nameof(request));
        }

        var endpoint = _endpointProvider.GetSearchEndpoint();
        var requestUri = BuildRequestUri(
            endpoint,
            queryParameters: new Dictionary<string, string?>
            {
                ["q"] = request.Query,
                ["limit"] = request.Limit?.ToString(),
            });

        using var httpRequest = CreateRequest(HttpMethod.Get, requestUri, endpoint.DefaultHeaders);
        var response = await SendForJsonAsync<SearchSkillsResponse>(endpoint.Operation, httpRequest, cancellationToken);
        response.Skills.Sort(static (left, right) => right.Installs.CompareTo(left.Installs));
        return response;
    }

    public async Task<WellKnownDiscoveryResponse> DiscoverWellKnownAsync(WellKnownDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var sourceUri))
        {
            throw new ArgumentException("Well-known discovery requires an absolute URI.", nameof(request));
        }

        var candidates = _endpointProvider.GetWellKnownDiscoveryEndpoints(sourceUri);
        OnlineApiValidationException? validationFailure = null;
        OnlineApiHttpException? httpFailure = null;

        foreach (var candidate in candidates)
        {
            using var httpRequest = CreateRequest(HttpMethod.Get, candidate.IndexUri);
            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                httpFailure = await CreateHttpExceptionAsync(OnlineApiOperation.WellKnownDiscovery, candidate.IndexUri, response, cancellationToken);
                continue;
            }

            WellKnownIndexDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                document = await JsonSerializer.DeserializeAsync<WellKnownIndexDocument>(stream, JsonOptions, cancellationToken)
                    ?? throw new OnlineApiValidationException(
                        OnlineApiOperation.WellKnownDiscovery,
                        "Well-known index payload was empty.",
                        candidate.IndexUri);
            }
            catch (JsonException exception)
            {
                validationFailure = new OnlineApiValidationException(
                    OnlineApiOperation.WellKnownDiscovery,
                    $"Well-known index payload was not valid JSON: {exception.Message}",
                    candidate.IndexUri,
                    exception);
                continue;
            }

            ValidateWellKnownDocument(document, candidate.IndexUri);
            return new WellKnownDiscoveryResponse
            {
                RequestedUri = sourceUri,
                ResolvedIndexUri = candidate.IndexUri,
                ResolvedBaseUri = candidate.ResolvedBaseUri,
                Skills = document.Skills,
            };
        }

        if (validationFailure is not null)
        {
            throw validationFailure;
        }

        if (httpFailure is not null)
        {
            throw httpFailure;
        }

        throw new OnlineApiException(
            OnlineApiOperation.WellKnownDiscovery,
            $"No well-known index could be discovered for '{request.SourceUrl}'.",
            sourceUri);
    }

    public async Task<AuditMetadataResponse> GetAuditMetadataAsync(AuditMetadataRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new ArgumentException("Audit source is required.", nameof(request));
        }

        if (request.Skills.Count == 0)
        {
            throw new ArgumentException("At least one skill is required for audit requests.", nameof(request));
        }

        var endpoint = _endpointProvider.GetAuditEndpoint();
        var requestUri = BuildRequestUri(
            endpoint,
            queryParameters: new Dictionary<string, string?>
            {
                ["source"] = request.Source,
                ["skills"] = string.Join(',', request.Skills),
            });

        using var httpRequest = CreateRequest(HttpMethod.Get, requestUri, endpoint.DefaultHeaders);
        var payload = await SendForJsonAsync<Dictionary<string, Dictionary<string, AuditSkillMetadata>>>(endpoint.Operation, httpRequest, cancellationToken);
        return new AuditMetadataResponse { Sources = payload };
    }

    public async Task<TelemetryTrackResult> TrackTelemetryAsync(TelemetryEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = _endpointProvider.GetTelemetryEndpoint();
        var parameters = request.GetParameters().ToList();
        if (!string.IsNullOrWhiteSpace(_options.TelemetryVersion))
        {
            parameters.Add(new KeyValuePair<string, string>("v", _options.TelemetryVersion));
        }

        if (_options.TelemetryIsCi)
        {
            parameters.Add(new KeyValuePair<string, string>("ci", "1"));
        }

        var requestUri = BuildRequestUri(
            endpoint,
            queryParameters: parameters.Select(static pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
        if (_options.DisableTelemetry)
        {
            return new TelemetryTrackResult
            {
                IsSkipped = true,
                RequestUri = requestUri,
            };
        }

        using var httpRequest = CreateRequest(HttpMethod.Get, requestUri, endpoint.DefaultHeaders);
        await SendAsync(endpoint.Operation, httpRequest, cancellationToken);
        return new TelemetryTrackResult
        {
            IsSkipped = false,
            RequestUri = requestUri,
        };
    }

    public async Task<GitHubRepositoryMetadataResponse> GetGitHubRepositoryMetadataAsync(GitHubRepositoryMetadataRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGitHubRepositoryRequest(request.Owner, request.Repository, nameof(request));

        var endpoint = _endpointProvider.GetGitHubRepositoryEndpoint();
        var requestUri = BuildRequestUri(
            endpoint,
            routeValues: new Dictionary<string, string>
            {
                ["owner"] = request.Owner,
                ["repo"] = request.Repository,
            });

        using var httpRequest = CreateRequest(HttpMethod.Get, requestUri, endpoint.DefaultHeaders);
        ApplyGitHubAuthorization(httpRequest, request.Token);
        return await SendForJsonAsync<GitHubRepositoryMetadataResponse>(endpoint.Operation, httpRequest, cancellationToken);
    }

    public async Task<GitHubTreeMetadataResponse> GetGitHubTreeMetadataAsync(GitHubTreeMetadataRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGitHubRepositoryRequest(request.Owner, request.Repository, nameof(request));
        if (string.IsNullOrWhiteSpace(request.Branch))
        {
            throw new ArgumentException("GitHub branch is required.", nameof(request));
        }

        var endpoint = _endpointProvider.GetGitHubTreeEndpoint();
        var requestUri = BuildRequestUri(
            endpoint,
            routeValues: new Dictionary<string, string>
            {
                ["owner"] = request.Owner,
                ["repo"] = request.Repository,
                ["branch"] = request.Branch,
            },
            queryParameters: new Dictionary<string, string?>
            {
                ["recursive"] = request.Recursive ? "1" : null,
            });

        using var httpRequest = CreateRequest(HttpMethod.Get, requestUri, endpoint.DefaultHeaders);
        ApplyGitHubAuthorization(httpRequest, request.Token);
        return await SendForJsonAsync<GitHubTreeMetadataResponse>(endpoint.Operation, httpRequest, cancellationToken);
    }

    private void ApplyGitHubAuthorization(HttpRequestMessage request, string? tokenOverride)
    {
        var token = string.IsNullOrWhiteSpace(tokenOverride) ? _options.GitHubToken : tokenOverride;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private async Task SendAsync(OnlineApiOperation operation, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateHttpExceptionAsync(operation, request.RequestUri!, response, cancellationToken);
        }
    }

    private async Task<T> SendForJsonAsync<T>(OnlineApiOperation operation, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateHttpExceptionAsync(operation, request.RequestUri!, response, cancellationToken);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                ?? throw new OnlineApiValidationException(operation, "The response payload was empty.", request.RequestUri);
        }
        catch (JsonException exception)
        {
            throw new OnlineApiValidationException(
                operation,
                $"The response payload could not be deserialized: {exception.Message}",
                request.RequestUri,
                exception);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri requestUri, IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        if (headers is null)
        {
            return request;
        }

        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    private static async Task<OnlineApiHttpException> CreateHttpExceptionAsync(
        OnlineApiOperation operation,
        Uri requestUri,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? responseBody = null;
        if (response.Content is not null)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            responseBody = string.IsNullOrWhiteSpace(content)
                ? null
                : content[..Math.Min(content.Length, 512)];
        }

        return new OnlineApiHttpException(
            operation,
            $"{operation} request to '{requestUri}' failed with status code {(int)response.StatusCode} ({response.StatusCode}).",
            response.StatusCode,
            requestUri,
            responseBody);
    }

    private static Uri BuildRequestUri(
        OnlineApiEndpointProfile endpoint,
        IReadOnlyDictionary<string, string>? routeValues = null,
        IEnumerable<KeyValuePair<string, string?>>? queryParameters = null)
    {
        var path = endpoint.RelativePathTemplate;
        if (routeValues is not null)
        {
            foreach (var routeValue in routeValues)
            {
                path = path.Replace("{" + routeValue.Key + "}", Uri.EscapeDataString(routeValue.Value), StringComparison.Ordinal);
            }
        }

        var builder = new UriBuilder(new Uri(endpoint.BaseUri, path));
        if (queryParameters is not null)
        {
            var query = string.Join(
                "&",
                queryParameters
                    .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
            builder.Query = query;
        }

        return builder.Uri;
    }

    private static void ValidateGitHubRepositoryRequest(string owner, string repository, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("GitHub owner is required.", parameterName);
        }

        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new ArgumentException("GitHub repository is required.", parameterName);
        }
    }

    private static void ValidateWellKnownDocument(WellKnownIndexDocument document, Uri requestUri)
    {
        if (document.Skills.Count == 0)
        {
            throw new OnlineApiValidationException(
                OnlineApiOperation.WellKnownDiscovery,
                "Well-known index must contain a non-empty 'skills' array.",
                requestUri);
        }

        foreach (var skill in document.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Name))
            {
                throw new OnlineApiValidationException(
                    OnlineApiOperation.WellKnownDiscovery,
                    "Well-known skill entries must include 'name'.",
                    requestUri);
            }

            if (!IsValidWellKnownSkillName(skill.Name))
            {
                throw new OnlineApiValidationException(
                    OnlineApiOperation.WellKnownDiscovery,
                    $"Well-known skill name '{skill.Name}' must be lowercase alphanumeric or hyphenated.",
                    requestUri);
            }

            if (string.IsNullOrWhiteSpace(skill.Description))
            {
                throw new OnlineApiValidationException(
                    OnlineApiOperation.WellKnownDiscovery,
                    $"Well-known skill '{skill.Name}' must include 'description'.",
                    requestUri);
            }

            if (skill.Files.Count == 0)
            {
                throw new OnlineApiValidationException(
                    OnlineApiOperation.WellKnownDiscovery,
                    $"Well-known skill '{skill.Name}' must include at least one file.",
                    requestUri);
            }

            if (!skill.Files.Any(static file => file.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            {
                throw new OnlineApiValidationException(
                    OnlineApiOperation.WellKnownDiscovery,
                    $"Well-known skill '{skill.Name}' must include 'SKILL.md'.",
                    requestUri);
            }

            foreach (var file in skill.Files)
            {
                if (file.StartsWith("/", StringComparison.Ordinal) ||
                    file.StartsWith("\\", StringComparison.Ordinal) ||
                    file.Contains("..", StringComparison.Ordinal))
                {
                    throw new OnlineApiValidationException(
                        OnlineApiOperation.WellKnownDiscovery,
                        $"Well-known skill '{skill.Name}' contains an invalid file path '{file}'.",
                        requestUri);
                }
            }
        }
    }

    private static bool IsValidWellKnownSkillName(string name)
    {
        if (name.Length == 1)
        {
            return char.IsAsciiLetterLower(name[0]) || char.IsDigit(name[0]);
        }

        return System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$");
    }
}
