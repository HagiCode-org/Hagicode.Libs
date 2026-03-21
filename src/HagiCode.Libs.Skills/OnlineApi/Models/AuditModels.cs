using System.Text.Json.Serialization;

namespace HagiCode.Libs.Skills.OnlineApi.Models;

public sealed class AuditMetadataRequest
{
    public string Source { get; init; } = string.Empty;

    public List<string> Skills { get; init; } = [];
}

public sealed class AuditMetadataResponse
{
    public Dictionary<string, Dictionary<string, AuditSkillMetadata>> Sources { get; init; } = [];
}

public sealed class AuditSkillMetadata
{
    [JsonPropertyName("risk")]
    public string Risk { get; init; } = string.Empty;

    [JsonPropertyName("alerts")]
    public int? Alerts { get; init; }

    [JsonPropertyName("score")]
    public int? Score { get; init; }

    [JsonPropertyName("analyzedAt")]
    public string AnalyzedAt { get; init; } = string.Empty;
}
