namespace HagiCode.Libs.Skills.OnlineApi.Providers;

public sealed record OnlineApiEndpointProfile(
    OnlineApiOperation Operation,
    Uri BaseUri,
    string RelativePathTemplate,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null);

public sealed record WellKnownDiscoveryEndpointCandidate(Uri IndexUri, Uri ResolvedBaseUri);
