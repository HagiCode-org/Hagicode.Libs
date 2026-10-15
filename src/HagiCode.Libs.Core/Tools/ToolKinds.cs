namespace HagiCode.Libs.Core.Tools;

/// <summary>
/// The canonical tool-call kind vocabulary. The values are an open list for consumers: a value outside
/// <see cref="All" /> is treated as <see cref="Other" />, see <see cref="Normalize" />.
/// </summary>
public static class ToolKinds
{
    /// <summary>A shell command (Claude Bash, Codex command_execution, ACP execute).</summary>
    public const string Shell = "shell";

    /// <summary>Reads a file without changing it.</summary>
    public const string Read = "read";

    /// <summary>Creates or overwrites a whole file.</summary>
    public const string Write = "write";

    /// <summary>Changes part of a file by replacing old text with new text.</summary>
    public const string Edit = "edit";

    /// <summary>Searches file contents.</summary>
    public const string Search = "search";

    /// <summary>Finds files by name or path pattern.</summary>
    public const string Glob = "glob";

    /// <summary>Fetches a URL or web resource.</summary>
    public const string Fetch = "fetch";

    /// <summary>Starts a subagent or delegated task.</summary>
    public const string Subagent = "subagent";

    /// <summary>Updates a task list.</summary>
    public const string Todo = "todo";

    /// <summary>A call to a Model Context Protocol server tool. The server identifier travels with it.</summary>
    public const string Mcp = "mcp";

    /// <summary>Any call that has no recognizable kind, including values outside this vocabulary.</summary>
    public const string Other = "other";

    /// <summary>All canonical kinds in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Shell, Read, Write, Edit, Search, Glob, Fetch, Subagent, Todo, Mcp, Other
    ];

    /// <summary>
    /// Returns the canonical kind for a trimmed, case-insensitive match against <see cref="All" />, and
    /// <see cref="Other" /> for anything else, including null and empty values.
    /// </summary>
    /// <param name="kind">A kind reported by a provider or stored in metadata.</param>
    public static string Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return Other;
        }

        var trimmed = kind.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return Other;
    }
}
