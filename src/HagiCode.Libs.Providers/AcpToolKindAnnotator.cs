using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Tools;

namespace HagiCode.Libs.Providers;

/// <summary>
/// Adds the canonical tool kind to an ACP <c>tool_call</c> or <c>tool_call_update</c> update. The provider's own
/// <c>kind</c>, <c>title</c> and <c>rawInput</c> are kept exactly as received, so the classified value is an addition.
/// </summary>
internal static class AcpToolKindAnnotator
{
    /// <summary>
    /// Returns a copy of the update with <c>toolKind</c> set, and <c>toolServer</c> for MCP calls. A non-object update is
    /// returned unchanged.
    /// </summary>
    /// <param name="update">The ACP update object.</param>
    public static JsonElement Annotate(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object)
        {
            return update;
        }

        var resolution = ToolKindClassifier.Resolve(
            providerKind: ReadString(update, "kind"),
            rawName: ReadString(update, "title"),
            inputJson: update.TryGetProperty("rawInput", out var rawInput) && rawInput.ValueKind != JsonValueKind.Null
                ? rawInput.GetRawText()
                : null);

        if (JsonNode.Parse(update.GetRawText()) is not JsonObject node)
        {
            return update;
        }

        node["toolKind"] = resolution.Kind;
        if (resolution.Server is not null)
        {
            node["toolServer"] = resolution.Server;
        }

        return JsonSerializer.SerializeToElement(node);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
