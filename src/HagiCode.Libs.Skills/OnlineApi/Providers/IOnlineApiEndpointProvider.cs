namespace HagiCode.Libs.Skills.OnlineApi.Providers;

public interface IOnlineApiEndpointProvider
{
    OnlineApiEndpointProfile GetSearchEndpoint();

    OnlineApiEndpointProfile GetAuditEndpoint();

    OnlineApiEndpointProfile GetTelemetryEndpoint();

    OnlineApiEndpointProfile GetGitHubRepositoryEndpoint();

    OnlineApiEndpointProfile GetGitHubTreeEndpoint();

    IReadOnlyList<WellKnownDiscoveryEndpointCandidate> GetWellKnownDiscoveryEndpoints(Uri sourceUri);
}
