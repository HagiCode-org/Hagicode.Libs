using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Response of <c>GET /api/info</c>. OpenCode 2.0 has no <c>healthy</c> flag: a successful JSON response is the health signal.
/// </summary>
public sealed class OpenCodeHealthResponse
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("pid")]
    public long? ProcessId { get; init; }

    [JsonPropertyName("urls")]
    public List<string> Urls { get; init; } = [];

    [JsonIgnore]
    public bool Healthy => !string.IsNullOrWhiteSpace(Version);
}

/// <summary>
/// Wraps a single OpenCode 2.0 payload: <c>{ "data": ... }</c>.
/// </summary>
public sealed class OpenCodeDataResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

public sealed class OpenCodeLocationRef
{
    [JsonPropertyName("directory")]
    public string Directory { get; init; } = string.Empty;
}

public sealed class OpenCodeProject
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("canonical")]
    public string? Canonical { get; init; }

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

    [JsonPropertyName("location")]
    public OpenCodeLocationRef? Location { get; init; }

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

/// <summary>
/// Response of <c>GET /api/session</c>: a page of sessions with a cursor.
/// </summary>
public sealed class OpenCodeSessionPage
{
    [JsonPropertyName("data")]
    public List<OpenCodeSession> Data { get; init; } = [];
}

/// <summary>
/// One entry of <c>GET /api/fs/list</c>.
/// </summary>
public sealed class OpenCodeFileSystemEntry
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

public sealed class OpenCodeFileSystemListResponse
{
    [JsonPropertyName("data")]
    public List<OpenCodeFileSystemEntry> Data { get; init; } = [];
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

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenCodeLocationRef? Location { get; init; }

    [JsonPropertyName("permissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Permissions { get; init; }
}

/// <summary>
/// The in-memory model selection (<c>providerID</c> + model id) used by providers.
/// </summary>
public sealed class OpenCodeModelSelection
{
    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;
}

/// <summary>
/// Model reference as OpenCode 2.0 sends it: <c>{ "id": ..., "providerID": ... }</c>.
/// </summary>
public sealed class OpenCodeModelRef
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("providerID")]
    public string ProviderId { get; init; } = string.Empty;

    public static OpenCodeModelRef From(OpenCodeModelSelection selection) => new()
    {
        Id = selection.ModelId,
        ProviderId = selection.ProviderId,
    };
}

/// <summary>
/// Body of <c>POST /api/session/{id}/model</c>.
/// </summary>
public sealed class OpenCodeSessionModelRequest
{
    [JsonPropertyName("model")]
    public OpenCodeModelRef Model { get; init; } = new();
}

/// <summary>
/// Body of <c>POST /api/session/{id}/prompt</c> (OpenCode 2.0). The model is set on the session, not on the prompt.
/// </summary>
public sealed class OpenCodeSessionPromptRequest
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageId { get; init; }

    [JsonPropertyName("delivery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Delivery { get; init; }

    public static OpenCodeSessionPromptRequest FromText(string text)
    {
        return new OpenCodeSessionPromptRequest
        {
            Text = text,
        };
    }
}

/// <summary>
/// The queued user inbox item that <c>POST /api/session/{id}/prompt</c> returns (<c>Session.Inbox.User</c>).
/// It is an acknowledgement only; the assistant reply arrives on the event stream.
/// </summary>
public sealed class OpenCodeInboxUser
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("sessionID")]
    public string? SessionId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("delivery")]
    public string? Delivery { get; init; }
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

        // A failed turn is reported as an "error" part; it must not read like a successful empty reply.
        var lead = partTypes.Any(static type => string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            ? "OpenCode reported a failed turn with no readable text content."
            : "OpenCode returned a successful response with no readable text content.";

        return $"{lead} messageId={messageId}; role={role}; partTypes={partTypeSummary}; diagnostics={diagnosticSummary}";
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

/// <summary>
/// One message from <c>GET /api/event</c>. <see cref="Data"/> holds the whole <c>V2EventEncoded</c> JSON object.
/// </summary>
public sealed class OpenCodeEventEnvelope
{
    public string? EventName { get; init; }

    public JsonElement Data { get; init; }

    /// <summary>
    /// Gets the event type, for example <c>session.text.ended</c>.
    /// </summary>
    public string? EventType => TryGetString(Data, "type");

    /// <summary>
    /// Gets the session the event belongs to (<c>data.sessionID</c>), when present.
    /// </summary>
    public string? SessionId
    {
        get
        {
            return Data.ValueKind == JsonValueKind.Object &&
                   Data.TryGetProperty("data", out var payload) &&
                   payload.ValueKind == JsonValueKind.Object
                ? TryGetString(payload, "sessionID")
                : null;
        }
    }

    /// <summary>
    /// Gets the event payload (<c>data.data</c>), or a default element when absent.
    /// </summary>
    public JsonElement Payload
    {
        get
        {
            return Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("data", out var payload)
                ? payload
                : default;
        }
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

public class OpenCodeApiException : InvalidOperationException
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

/// <summary>
/// The OpenCode server rejected the credentials (HTTP 401), or no password was supplied for a protected runtime.
/// The message never contains the password.
/// </summary>
public sealed class OpenCodeAuthenticationException : OpenCodeApiException
{
    public OpenCodeAuthenticationException(string requestUri)
        : base(
            HttpStatusCode.Unauthorized,
            $"OpenCode API authentication failed for '{requestUri}' (HTTP 401). The OpenCode server password is missing or wrong; configure the server password for this runtime.")
    {
    }
}

/// <summary>
/// The target server does not serve the OpenCode 2.0 <c>/api/*</c> interface (for example it answered with the Web UI HTML shell).
/// </summary>
public sealed class OpenCodeUnsupportedApiException : OpenCodeApiException
{
    public OpenCodeUnsupportedApiException(string requestUri, string? bodyExcerpt)
        : base(
            HttpStatusCode.OK,
            $"OpenCode API request to '{requestUri}' returned a response that is not the OpenCode 2.0 API. The server does not expose the supported OpenCode API (OpenCode 2.0 is required).",
            bodyExcerpt)
    {
    }
}
