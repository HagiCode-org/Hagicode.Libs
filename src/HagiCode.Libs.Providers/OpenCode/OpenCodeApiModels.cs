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
/// <see cref="Files"/> carries inline attachments and is omitted when there are none.
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

    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OpenCodePromptFile>? Files { get; init; }

    public static OpenCodeSessionPromptRequest FromText(string text)
    {
        return new OpenCodeSessionPromptRequest
        {
            Text = text,
        };
    }

    /// <summary>
    /// Builds a prompt with inline files. An empty list produces the same request as <see cref="FromText"/>, so no <c>files</c> member is sent.
    /// </summary>
    public static OpenCodeSessionPromptRequest FromTextWithFiles(string text, IReadOnlyList<OpenCodePromptFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new OpenCodeSessionPromptRequest
        {
            Text = text,
            Files = files.Count == 0 ? null : files,
        };
    }

    /// <summary>
    /// Prints the id, the text length and the file count only. The text and the file data never appear.
    /// </summary>
    public override string ToString()
    {
        return $"OpenCodeSessionPromptRequest {{ Id = {MessageId ?? "(none)"}, Text.Length = {Text.Length}, Files = {Files?.Count ?? 0} }}";
    }
}

/// <summary>
/// One entry of the <c>files</c> array of a prompt (OpenCode 2.0): <c>{ "mime", "uri", "name" }</c>. <see cref="Uri"/> is a
/// <c>data:&lt;mime&gt;;base64,…</c> URL. OpenCode 2.0 rejects a bare string entry and a <c>url</c> key.
/// </summary>
public sealed class OpenCodePromptFile
{
    [JsonPropertyName("mime")]
    public string Mime { get; init; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Prints the media type, the name and the length of the data URL. The data URL itself never appears.
    /// </summary>
    public override string ToString()
    {
        return $"OpenCodePromptFile {{ Mime = {Mime}, Name = {Name}, Uri.Length = {Uri.Length} }}";
    }
}

/// <summary>
/// One entry of <c>GET /api/model</c>: the provider and model ids, and the input kinds the model accepts from
/// <c>capabilities.input</c>.
/// </summary>
public sealed class OpenCodeModelInfo
{
    public string ProviderId { get; init; } = string.Empty;

    public string Id { get; init; } = string.Empty;

    /// <summary>The input kinds (for example <c>text</c>, <c>image</c>). Empty when OpenCode reports none.</summary>
    public IReadOnlyList<string> InputCapabilities { get; init; } = [];

    /// <summary>True when <c>image</c> is among the input kinds.</summary>
    public bool AcceptsImage => InputCapabilities.Any(static input => string.Equals(input, "image", StringComparison.OrdinalIgnoreCase));
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
    /// Gets the session the event belongs to: <c>data.sessionID</c>, or <c>data.form.sessionID</c> for a form event
    /// (OpenCode 2.0.32 <c>form.created</c> carries the session inside the form only). Null when neither is present.
    /// </summary>
    public string? SessionId
    {
        get
        {
            if (Data.ValueKind != JsonValueKind.Object ||
                !Data.TryGetProperty("data", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return TryGetString(payload, "sessionID") ??
                   (payload.TryGetProperty("form", out var form) ? TryGetString(form, "sessionID") : null);
        }
    }

    /// <summary>
    /// Gets the form carried by a <c>form.*</c> event (<c>data.form</c>), or null when the event carries none.
    /// The <c>form.replied</c> and <c>form.cancelled</c> events carry only the form id, so they return null.
    /// </summary>
    public OpenCodeFormInfo? Form
    {
        get
        {
            if (Payload.ValueKind != JsonValueKind.Object ||
                !Payload.TryGetProperty("form", out var form) ||
                form.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            try
            {
                return form.Deserialize<OpenCodeFormInfo>(OpenCodeFormJson.Options);
            }
            catch (JsonException)
            {
                // A form that does not match the model cannot be answered; the turn treats it as absent.
                return null;
            }
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

/// <summary>
/// The kinds of OpenCode form that the client knows about (<c>form.metadata.kind</c>).
/// </summary>
public static class OpenCodeFormKinds
{
    /// <summary>The form the built-in <c>question</c> tool opens. The client answers these.</summary>
    public const string Question = "question";

    /// <summary>The form an MCP server opens for elicitation. The client leaves these untouched.</summary>
    public const string Mcp = "mcp";
}

/// <summary>
/// The lifecycle names of an OpenCode form (<c>form.state</c>). A pending form accepts a reply or a cancel.
/// </summary>
public static class OpenCodeFormStates
{
    public const string Pending = "pending";

    public const string Replied = "replied";

    public const string Cancelled = "cancelled";
}

/// <summary>
/// One option of an OpenCode form field (<c>form.fields[].options[]</c>).
/// </summary>
public sealed class OpenCodeFormOption
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// One field of an OpenCode form (<c>form.fields[]</c>). A question form has one field per question, keyed <c>q0</c>..<c>qN</c>
/// in the order the agent asked. A <c>string</c> field takes one string, a <c>multiselect</c> field takes a list of strings.
/// </summary>
public sealed class OpenCodeFormField
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The question header (the question tool's <c>header</c>).</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The question text (the question tool's <c>question</c>).</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Either <c>string</c> (single choice) or <c>multiselect</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("options")]
    public List<OpenCodeFormOption> Options { get; init; } = [];

    /// <summary>Whether the user may type a value that is not one of the options.</summary>
    [JsonPropertyName("custom")]
    public bool Custom { get; init; }
}

/// <summary>
/// The tool call that opened a form (<c>form.metadata.tool</c>). Diagnostic only; the form id identifies the form.
/// </summary>
public sealed class OpenCodeFormToolReference
{
    [JsonPropertyName("messageID")]
    public string? MessageId { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>
/// The metadata of an OpenCode form (<c>form.metadata</c>). <see cref="Kind"/> decides who answers the form.
/// </summary>
public sealed class OpenCodeFormMetadata
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("tool")]
    public OpenCodeFormToolReference? Tool { get; init; }
}

/// <summary>
/// An OpenCode form as <c>form.created</c> and <c>GET /api/session/{id}/form</c> report it.
/// </summary>
public sealed class OpenCodeFormInfo
{
    /// <summary>The form id (<c>frm_...</c>). It is the key of the reply and cancel routes.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("sessionID")]
    public string? SessionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("metadata")]
    public OpenCodeFormMetadata? Metadata { get; init; }

    [JsonPropertyName("fields")]
    public List<OpenCodeFormField> Fields { get; init; } = [];

    /// <summary>
    /// The form state as OpenCode reports it, when the form detail carries one. A form detail reports <c>cancelled</c>
    /// after a cancel; the state may be a plain string or an object with a <c>status</c>. Use <see cref="StateName"/>.
    /// </summary>
    [JsonPropertyName("state")]
    public JsonElement? State { get; init; }

    /// <summary>The form kind (<c>metadata.kind</c>), or null when the form carries none.</summary>
    [JsonIgnore]
    public string? Kind => Metadata?.Kind;

    /// <summary>
    /// The state name (<see cref="OpenCodeFormStates"/>) read from <see cref="State"/>, or null when the form carries no state.
    /// </summary>
    [JsonIgnore]
    public string? StateName
    {
        get
        {
            if (State is not { } state)
            {
                return null;
            }

            return state.ValueKind switch
            {
                JsonValueKind.String => state.GetString(),
                JsonValueKind.Object when state.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String => status.GetString(),
                _ => null,
            };
        }
    }
}

/// <summary>
/// Body of <c>POST /api/session/{id}/form/{formId}/reply</c>. The answer is keyed by field key; a <c>string</c> field takes a
/// string and a <c>multiselect</c> field takes a list of strings.
/// </summary>
public sealed class OpenCodeFormReplyRequest
{
    [JsonPropertyName("answer")]
    public IReadOnlyDictionary<string, object> Answer { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
}

/// <summary>
/// Body of <c>POST /api/session/{id}/interrupt</c>: whether OpenCode interrupted the session.
/// </summary>
public sealed class OpenCodeSessionInterruptResponse
{
    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; init; }
}

/// <summary>
/// What OpenCode did with a reply or a cancel of a form.
/// </summary>
public enum OpenCodeFormOutcome
{
    /// <summary>OpenCode accepted the reply or cancel (HTTP 204).</summary>
    Accepted,

    /// <summary>The form was already settled (<c>409 FormAlreadySettledError</c>). The question is closed; nothing is left to do.</summary>
    AlreadySettled,
}

/// <summary>
/// Reads the OpenCode JSON settings used by the form models.
/// </summary>
internal static class OpenCodeFormJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
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
