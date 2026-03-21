using System.Text.Json.Serialization;

namespace HagiCode.Libs.Skills.OnlineApi.Models;

public sealed class GitHubRepositoryMetadataRequest
{
    public string Owner { get; init; } = string.Empty;

    public string Repository { get; init; } = string.Empty;

    public string? Token { get; init; }
}

public sealed class GitHubRepositoryMetadataResponse
{
    [JsonPropertyName("private")]
    public bool Private { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; init; } = string.Empty;

    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; init; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; init; } = string.Empty;
}

public sealed class GitHubTreeMetadataRequest
{
    public string Owner { get; init; } = string.Empty;

    public string Repository { get; init; } = string.Empty;

    public string Branch { get; init; } = string.Empty;

    public bool Recursive { get; init; } = true;

    public string? Token { get; init; }
}

public sealed class GitHubTreeMetadataResponse
{
    [JsonPropertyName("sha")]
    public string Sha { get; init; } = string.Empty;

    [JsonPropertyName("tree")]
    public List<GitHubTreeEntry> Tree { get; init; } = [];
}

public sealed class GitHubTreeEntry
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("sha")]
    public string Sha { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public int? Size { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}
