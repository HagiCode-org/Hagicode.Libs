using System.Text.Json.Serialization;

namespace HagiCode.Libs.Skills.OnlineApi.Models;

public sealed class WellKnownDiscoveryRequest
{
    public string SourceUrl { get; init; } = string.Empty;
}

public sealed class WellKnownDiscoveryResponse
{
    public required Uri RequestedUri { get; init; }

    public required Uri ResolvedIndexUri { get; init; }

    public required Uri ResolvedBaseUri { get; init; }

    public List<WellKnownSkillIndexEntry> Skills { get; init; } = [];
}

public sealed class WellKnownIndexDocument
{
    [JsonPropertyName("skills")]
    public List<WellKnownSkillIndexEntry> Skills { get; init; } = [];
}

public sealed class WellKnownSkillIndexEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("files")]
    public List<string> Files { get; init; } = [];
}
