using System.Text.Json.Serialization;

namespace HagiCode.Libs.Skills.OnlineApi.Models;

public sealed class SearchSkillsRequest
{
    public string Query { get; init; } = string.Empty;

    public int? Limit { get; init; } = 10;
}

public sealed class SearchSkillsResponse
{
    [JsonPropertyName("skills")]
    public List<SearchSkillSummary> Skills { get; init; } = [];
}

public sealed class SearchSkillSummary
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("installs")]
    public int Installs { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}
