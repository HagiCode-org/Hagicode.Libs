using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HagiCode.Libs.Providers.OpenCode;

public sealed class OpenCodeHealthResponse
{
    [JsonPropertyName("healthy")]
    public bool Healthy { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed class OpenCodeProject
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("worktree")]
    public string Worktree { get; init; } = string.Empty;

    [JsonPropertyName("vcs")]
    public string? Vcs { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("icon")]
    public JsonElement? Icon { get; init; }

    [JsonPropertyName("commands")]
    public JsonElement? Commands { get; init; }

    [JsonPropertyName("time")]
    public JsonElement? Time { get; init; }

    [JsonPropertyName("sandboxes")]
    public List<string> Sandboxes { get; init; } = [];
}

public sealed class OpenCodeSession
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("projectID")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("workspaceID")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("directory")]
    public string? Directory { get; init; }

    [JsonPropertyName("parentID")]
    public string? ParentId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("summary")]
    public JsonElement? Summary { get; init; }

    [JsonPropertyName("share")]
    public JsonElement? Share { get; init; }

    [JsonPropertyName("time")]
    public JsonElement? Time { get; init; }

    [JsonPropertyName("permission")]
    public JsonElement? Permission { get; init; }

    [JsonPropertyName("revert")]
    public JsonElement? Revert { get; init; }
}

public sealed class OpenCodeFileNode
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("mime")]
    public string? Mime { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("children")]
    public List<OpenCodeFileNode> Children { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed class OpenCodeSessionCreateRequest
{
    [JsonPropertyName("parentID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("permission")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Permission { get; init; }
}

public sealed class OpenCodeModelSelection
{
    [JsonPropertyName("providerID")]
    public string ProviderId { get; init; } = string.Empty;

    [JsonPropertyName("modelID")]
    public string ModelId { get; init; } = string.Empty;
}

public sealed class OpenCodePromptPartInput
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("mime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mime { get; init; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Agent { get; init; }

    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public static OpenCodePromptPartInput TextPart(string text) => new()
    {
        Type = "text",
        Text = text,
    };
}

public sealed class OpenCodeSessionPromptRequest
{
    [JsonPropertyName("messageID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageId { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenCodeModelSelection? Model { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Agent { get; init; }

    [JsonPropertyName("noReply")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NoReply { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, bool>? Tools { get; init; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Format { get; init; }

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

    [JsonPropertyName("variant")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variant { get; init; }

    [JsonPropertyName("parts")]
    public List<OpenCodePromptPartInput> Parts { get; init; } = [];

    public static OpenCodeSessionPromptRequest FromText(
        string text,
        OpenCodeModelSelection? model = null,
        string? agent = null)
    {
        return new OpenCodeSessionPromptRequest
        {
            Model = model,
            Agent = agent,
            Parts = [OpenCodePromptPartInput.TextPart(text)],
        };
    }
}

public sealed class OpenCodeMessageEnvelope
{
    [JsonPropertyName("info")]
    public JsonElement Info { get; init; }

    [JsonPropertyName("parts")]
    public List<JsonElement> Parts { get; init; } = [];

    public string? MessageId => TryGetString(Info, "id");

    public string? SessionId => TryGetString(Info, "sessionID") ?? TryGetString(Info, "sessionId");

    public string? Role => TryGetString(Info, "role");

    public string GetTextContent()
    {
        var segments = new List<string>();
        foreach (var part in Parts)
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!part.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!string.Equals(typeNode.GetString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (part.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String)
            {
                var text = textNode.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    segments.Add(text);
                }
            }
        }

        return string.Join(Environment.NewLine, segments);
    }

    public string BuildDiagnosticSummary()
    {
        var partTypes = new List<string>();
        var diagnostics = new List<string>();

        foreach (var part in Parts)
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var partType = TryGetString(part, "type");
            if (!string.IsNullOrWhiteSpace(partType))
            {
                partTypes.Add(partType);
            }

            AddDiagnosticCandidate(diagnostics, partType, part, "message");
            AddDiagnosticCandidate(diagnostics, partType, part, "reason");
            AddDiagnosticCandidate(diagnostics, partType, part, "error");
            AddDiagnosticCandidate(diagnostics, partType, part, "text");

            if (part.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
            {
                AddDiagnosticCandidate(diagnostics, partType, state, "status");
                AddDiagnosticCandidate(diagnostics, partType, state, "message");
                AddDiagnosticCandidate(diagnostics, partType, state, "reason");
                AddDiagnosticCandidate(diagnostics, partType, state, "error");
            }
        }

        var messageId = MessageId ?? "(unknown)";
        var role = Role ?? "unknown";
        var partTypeSummary = partTypes.Count == 0
            ? "(none)"
            : string.Join(", ", partTypes.Distinct(StringComparer.OrdinalIgnoreCase));
        var diagnosticSummary = diagnostics.Count == 0
            ? "No diagnostic text was present in the OpenCode response envelope."
            : string.Join(" | ", diagnostics.Distinct(StringComparer.Ordinal));

        return $"OpenCode returned a successful response with no readable text content. messageId={messageId}; role={role}; partTypes={partTypeSummary}; diagnostics={diagnosticSummary}";
    }

    private static void AddDiagnosticCandidate(
        ICollection<string> diagnostics,
        string? partType,
        JsonElement element,
        string propertyName)
    {
        var value = TryGetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(partType) ? propertyName : $"{partType}.{propertyName}";
        diagnostics.Add($"{label}={value.Trim()}");
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }
}

public sealed class OpenCodeEventEnvelope
{
    public string? EventName { get; init; }

    public JsonElement Data { get; init; }
}

public sealed class OpenCodeApiException : InvalidOperationException
{
    public OpenCodeApiException(HttpStatusCode statusCode, string message, string? responseBody = null)
        : base(responseBody is null ? message : $"{message} Response: {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }
}
