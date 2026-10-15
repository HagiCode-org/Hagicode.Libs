using System.Text.Json;

namespace HagiCode.Libs.Core.Tools;

/// <summary>
/// The result of classifying one tool call: a canonical <see cref="ToolKinds" /> value and, for MCP calls, the server.
/// </summary>
/// <param name="Kind">A value from <see cref="ToolKinds.All" />; never empty.</param>
/// <param name="Server">The MCP server identifier. Null for every call that is not an MCP call.</param>
public sealed record ToolKindResolution(string Kind, string? Server = null);

/// <summary>
/// Resolves the canonical kind of a tool call. The rules run in this order, and the first match wins:
/// <list type="number">
/// <item>An explicit provider kind other than <c>other</c>: <c>execute</c> maps to shell, <c>read</c>, <c>edit</c>,
/// <c>search</c> and <c>fetch</c> map to themselves, vocabulary values pass through, and <c>delete</c>, <c>move</c>,
/// <c>think</c>, <c>switch_mode</c> or any unknown value map to <c>other</c>.</item>
/// <item>MCP identity: a provided server, or a <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> name, gives <c>mcp</c>.</item>
/// <item>A case-insensitive exact alias on the raw tool name (Claude, Codex and the names seen in fixtures).</item>
/// <item>A deliberately narrow input shape: a string <c>command</c> gives shell, old and new text give edit, <c>url</c>
/// gives fetch, <c>pattern</c> gives search. A bare path infers nothing.</item>
/// <item>Refinement: an edit with a path and full <c>content</c> but no old or new text becomes <c>write</c>.</item>
/// <item>Otherwise <c>other</c>.</item>
/// </list>
/// </summary>
public static class ToolKindClassifier
{
    private const string McpPrefix = "mcp__";

    private static readonly IReadOnlyDictionary<string, string> ExplicitProviderKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["execute"] = ToolKinds.Shell,
            ["read"] = ToolKinds.Read,
            ["edit"] = ToolKinds.Edit,
            ["search"] = ToolKinds.Search,
            ["fetch"] = ToolKinds.Fetch,
            ["delete"] = ToolKinds.Other,
            ["move"] = ToolKinds.Other,
            ["think"] = ToolKinds.Other,
            ["switch_mode"] = ToolKinds.Other,
        };

    // Only names that a fixture exercises belong here. Matching is case-insensitive, so the lowercase names that Pi and
    // OMP report (bash, write, read, edit, grep) resolve through the Claude entries.
    private static readonly IReadOnlyDictionary<string, string> RawNameAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bash"] = ToolKinds.Shell,
            ["Read"] = ToolKinds.Read,
            ["Write"] = ToolKinds.Write,
            ["Edit"] = ToolKinds.Edit,
            ["Glob"] = ToolKinds.Glob,
            ["Grep"] = ToolKinds.Search,
            ["WebSearch"] = ToolKinds.Search,
            ["WebFetch"] = ToolKinds.Fetch,
            ["Task"] = ToolKinds.Subagent,
            ["TodoWrite"] = ToolKinds.Todo,
            ["command_execution"] = ToolKinds.Shell,
        };

    /// <summary>
    /// Classifies one tool call.
    /// </summary>
    /// <param name="providerKind">The kind the provider reported, such as an ACP <c>kind</c>. Null when the provider reports none.</param>
    /// <param name="rawName">The tool name exactly as the provider reported it. Null when unknown.</param>
    /// <param name="inputJson">The tool input as a JSON object. Null, empty or invalid JSON counts as no input and never throws.</param>
    /// <param name="server">An MCP server the provider reported separately from the tool name. Null when not reported.</param>
    /// <returns>The canonical kind, plus the server for MCP calls.</returns>
    public static ToolKindResolution Resolve(string? providerKind, string? rawName, string? inputJson, string? server = null)
    {
        var name = NormalizeOptional(rawName);
        var reportedServer = NormalizeOptional(server);

        var explicitKind = NormalizeOptional(providerKind);
        if (explicitKind is not null && !string.Equals(explicitKind, ToolKinds.Other, StringComparison.OrdinalIgnoreCase))
        {
            var mapped = MapExplicitProviderKind(explicitKind);
            return Finish(mapped, mapped == ToolKinds.Mcp ? reportedServer ?? ParseMcpServer(name) : null, inputJson);
        }

        if (reportedServer is not null || (name is not null && name.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new ToolKindResolution(ToolKinds.Mcp, reportedServer ?? ParseMcpServer(name));
        }

        if (name is not null && RawNameAliases.TryGetValue(name, out var aliasKind))
        {
            return Finish(aliasKind, null, inputJson);
        }

        var shapeKind = InferFromInputShape(inputJson);
        if (shapeKind is not null)
        {
            return Finish(shapeKind, null, inputJson);
        }

        return new ToolKindResolution(ToolKinds.Other);
    }

    private static string MapExplicitProviderKind(string providerKind)
    {
        return ExplicitProviderKinds.TryGetValue(providerKind, out var mapped)
            ? mapped
            : ToolKinds.Normalize(providerKind);
    }

    private static ToolKindResolution Finish(string kind, string? server, string? inputJson)
    {
        if (string.Equals(kind, ToolKinds.Edit, StringComparison.Ordinal) && IsWholeFileWrite(inputJson))
        {
            return new ToolKindResolution(ToolKinds.Write);
        }

        return new ToolKindResolution(kind, server);
    }

    /// <summary>
    /// Parses <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>. Returns null when either part is missing, so a malformed name keeps kind
    /// <c>mcp</c> without a server.
    /// </summary>
    private static string? ParseMcpServer(string? name)
    {
        if (name is null || !name.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = name[McpPrefix.Length..];
        var separator = rest.IndexOf("__", StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= rest.Length)
        {
            return null;
        }

        return rest[..separator];
    }

    private static string? InferFromInputShape(string? inputJson)
    {
        using var document = TryParseObject(inputJson);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        if (HasString(root, "command"))
        {
            return ToolKinds.Shell;
        }

        if (HasOldAndNewText(root))
        {
            return ToolKinds.Edit;
        }

        if (HasString(root, "url"))
        {
            return ToolKinds.Fetch;
        }

        if (HasString(root, "pattern"))
        {
            return ToolKinds.Search;
        }

        return null;
    }

    private static bool IsWholeFileWrite(string? inputJson)
    {
        using var document = TryParseObject(inputJson);
        if (document is null)
        {
            return false;
        }

        var root = document.RootElement;
        var hasPath = HasString(root, "path") || HasString(root, "file_path");
        var hasContent = HasString(root, "content");
        var hasOldOrNew = HasProperty(root, "oldText") || HasProperty(root, "old_string") ||
                          HasProperty(root, "newText") || HasProperty(root, "new_string");
        return hasPath && hasContent && !hasOldOrNew;
    }

    private static bool HasOldAndNewText(JsonElement root)
    {
        var hasOld = HasString(root, "oldText") || HasString(root, "old_string");
        var hasNew = HasString(root, "newText") || HasString(root, "new_string");
        return hasOld && hasNew;
    }

    private static bool HasString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String;
    }

    private static bool HasProperty(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out _);
    }

    private static JsonDocument? TryParseObject(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
