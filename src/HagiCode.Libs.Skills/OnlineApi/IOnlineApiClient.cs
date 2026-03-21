using HagiCode.Libs.Skills.OnlineApi.Models;

namespace HagiCode.Libs.Skills.OnlineApi;

public interface IOnlineApiClient
{
    Task<SearchSkillsResponse> SearchAsync(SearchSkillsRequest request, CancellationToken cancellationToken = default);

    Task<WellKnownDiscoveryResponse> DiscoverWellKnownAsync(WellKnownDiscoveryRequest request, CancellationToken cancellationToken = default);

    Task<AuditMetadataResponse> GetAuditMetadataAsync(AuditMetadataRequest request, CancellationToken cancellationToken = default);

    Task<TelemetryTrackResult> TrackTelemetryAsync(TelemetryEventRequest request, CancellationToken cancellationToken = default);

    Task<GitHubRepositoryMetadataResponse> GetGitHubRepositoryMetadataAsync(GitHubRepositoryMetadataRequest request, CancellationToken cancellationToken = default);

    Task<GitHubTreeMetadataResponse> GetGitHubTreeMetadataAsync(GitHubTreeMetadataRequest request, CancellationToken cancellationToken = default);
}
