using Microsoft.Extensions.Options;

namespace HagiCode.Libs.Skills.OnlineApi.Providers;

public sealed class VercelOnlineApiEndpointProvider : IOnlineApiEndpointProvider
{
    private readonly OnlineApiOptions _options;

    public VercelOnlineApiEndpointProvider(IOptions<OnlineApiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public OnlineApiEndpointProfile GetSearchEndpoint() =>
        new(
            OnlineApiOperation.Search,
            EnsureTrailingSlash(_options.SearchBaseUri),
            "api/search");

    public OnlineApiEndpointProfile GetAuditEndpoint() =>
        new(
            OnlineApiOperation.Audit,
            EnsureTrailingSlash(_options.AuditBaseUri),
            "audit");

    public OnlineApiEndpointProfile GetTelemetryEndpoint() =>
        new(
            OnlineApiOperation.Telemetry,
            EnsureTrailingSlash(_options.TelemetryBaseUri),
            "t");

    public OnlineApiEndpointProfile GetGitHubRepositoryEndpoint() =>
        new(
            OnlineApiOperation.GitHubRepositoryMetadata,
            EnsureTrailingSlash(_options.GitHubBaseUri),
            "repos/{owner}/{repo}",
            CreateGitHubHeaders());

    public OnlineApiEndpointProfile GetGitHubTreeEndpoint() =>
        new(
            OnlineApiOperation.GitHubTreeMetadata,
            EnsureTrailingSlash(_options.GitHubBaseUri),
            "repos/{owner}/{repo}/git/trees/{branch}",
            CreateGitHubHeaders());

    public IReadOnlyList<WellKnownDiscoveryEndpointCandidate> GetWellKnownDiscoveryEndpoints(Uri sourceUri)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);

        if (!sourceUri.IsAbsoluteUri || (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Well-known discovery requires an absolute HTTP(S) URI.", nameof(sourceUri));
        }

        var rootBaseUri = new UriBuilder(sourceUri.Scheme, sourceUri.Host, sourceUri.IsDefaultPort ? -1 : sourceUri.Port).Uri;
        var relativePath = sourceUri.AbsolutePath.Trim('/');
        var candidates = new List<WellKnownDiscoveryEndpointCandidate>();

        if (relativePath.Length > 0)
        {
            var pathBaseUri = new Uri(rootBaseUri, relativePath.TrimEnd('/') + "/");
            candidates.Add(CreateWellKnownCandidate(pathBaseUri));
        }

        candidates.Add(CreateWellKnownCandidate(rootBaseUri));
        return candidates;
    }

    private IReadOnlyDictionary<string, string> CreateGitHubHeaders()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/vnd.github.v3+json",
            ["User-Agent"] = _options.GitHubUserAgent,
        };
    }

    private static WellKnownDiscoveryEndpointCandidate CreateWellKnownCandidate(Uri baseUri)
    {
        var normalizedBaseUri = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");
        var indexUri = new Uri(normalizedBaseUri, ".well-known/skills/index.json");
        return new WellKnownDiscoveryEndpointCandidate(indexUri, normalizedBaseUri);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/");
    }
}
