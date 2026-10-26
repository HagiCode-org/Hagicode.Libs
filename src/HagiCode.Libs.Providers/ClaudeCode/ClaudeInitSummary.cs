using System.Text.Json;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// The parts of Claude Code's <c>system</c> <c>init</c> message that diagnostics report.
/// </summary>
/// <param name="Version">The CLI version from <c>claude_code_version</c>, or <see langword="null" /> when absent.</param>
/// <param name="Plugins">The plugins the CLI registered for this run.</param>
/// <param name="Skills">The skill names the CLI exposed.</param>
/// <param name="Tools">The tool names the CLI exposed.</param>
internal sealed record ClaudeInitSummary(
    string? Version,
    IReadOnlyList<ClaudeInitPlugin> Plugins,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Tools)
{
    /// <summary>
    /// Gets a value indicating whether the CLI exposed the <c>Artifact</c> tool.
    /// </summary>
    public bool HasArtifactTool => Tools.Contains("Artifact", StringComparer.Ordinal);

    /// <summary>
    /// Gets how many skills start with each name prefix, where the prefix is the text before the first <c>:</c>.
    /// A skill without a <c>:</c> counts under <c>(none)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> SkillPrefixCounts => Skills
        .GroupBy(static skill => ClaudeInitSummary.PrefixOf(skill), StringComparer.Ordinal)
        .OrderBy(static group => group.Key, StringComparer.Ordinal)
        .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    /// <summary>
    /// Reads the init message tolerantly. A field that is missing or has an unexpected type counts as empty, and parsing
    /// never throws.
    /// </summary>
    /// <param name="message">The JSON content of the <c>system</c> message with subtype <c>init</c>.</param>
    /// <param name="summary">The summary, or an empty summary when <paramref name="message" /> is not an object.</param>
    /// <returns><see langword="true" /> when <paramref name="message" /> is a JSON object.</returns>
    public static bool TryParse(JsonElement message, out ClaudeInitSummary summary)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            summary = new ClaudeInitSummary(null, [], [], []);
            return false;
        }

        summary = new ClaudeInitSummary(
            Version: ReadString(message, "claude_code_version"),
            Plugins: ReadArray(message, "plugins").Select(ReadPlugin).OfType<ClaudeInitPlugin>().ToArray(),
            Skills: ReadStrings(message, "skills"),
            Tools: ReadStrings(message, "tools"));
        return true;
    }

    /// <summary>
    /// Compares the passed plugin directories with the plugins the CLI registered. A directory matches a plugin when their
    /// normalized paths are equal; the comparison ignores case on Windows.
    /// </summary>
    /// <param name="summary">The parsed init message.</param>
    /// <param name="pluginDirectories">The <c>--plugin-dir</c> values the run was started with.</param>
    /// <returns>The plugins registered without any skill under their prefix, and the directories with no registered plugin.</returns>
    public static ClaudeUnexposedPlugins FindUnexposedPlugins(ClaudeInitSummary summary, IReadOnlyList<string> pluginDirectories)
    {
        var registeredWithoutSkills = new List<ClaudeInitPlugin>();
        var passedWithoutRegistration = new List<string>();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var directory in pluginDirectories)
        {
            var normalizedDirectory = NormalizePath(directory);
            var plugin = summary.Plugins.FirstOrDefault(candidate =>
                candidate.Path is not null
                && string.Equals(NormalizePath(candidate.Path), normalizedDirectory, comparison));
            if (plugin is null)
            {
                passedWithoutRegistration.Add(directory);
                continue;
            }

            var prefix = plugin.Name + ":";
            if (!summary.Skills.Any(skill => skill.StartsWith(prefix, StringComparison.Ordinal)))
            {
                registeredWithoutSkills.Add(plugin);
            }
        }

        return new ClaudeUnexposedPlugins(registeredWithoutSkills, passedWithoutRegistration);
    }

    /// <summary>
    /// Describes the summary for logs: version, plugins by name and source, skill count, skill prefix counts, tool count
    /// and whether <c>Artifact</c> is present. Skill and tool names are not included.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        var pluginText = Plugins.Count == 0
            ? "(none)"
            : string.Join(", ", Plugins.Select(static plugin => $"{plugin.Name} ({plugin.Source ?? "unknown source"})"));
        var prefixText = SkillPrefixCounts.Count == 0
            ? "(none)"
            : string.Join(", ", SkillPrefixCounts.Select(static pair => $"{pair.Key}={pair.Value}"));

        return $"version={Version ?? "(unknown)"} plugins={pluginText} skills={Skills.Count} skillPrefixes={prefixText} tools={Tools.Count} artifact={HasArtifactTool}";
    }

    private static string PrefixOf(string skill)
    {
        var separator = skill.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? "(none)" : skill[..separator];
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string property)
    {
        return ReadArray(element, property)
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToArray();
    }

    private static ClaudeInitPlugin? ReadPlugin(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || ReadString(element, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        return new ClaudeInitPlugin(name, ReadString(element, "source"), ReadString(element, "path"));
    }
}

/// <summary>
/// A plugin the CLI reported in its init message.
/// </summary>
/// <param name="Name">The plugin name.</param>
/// <param name="Source">The plugin source, for example <c>hagi@inline</c>.</param>
/// <param name="Path">The plugin directory the CLI loaded.</param>
internal sealed record ClaudeInitPlugin(string Name, string? Source, string? Path);

/// <summary>
/// The result of matching passed plugin directories with the init message.
/// </summary>
/// <param name="RegisteredWithoutSkills">Plugins that are registered but expose no skill under their name prefix.</param>
/// <param name="PassedWithoutRegistration">Passed plugin directories that no registered plugin matches.</param>
internal sealed record ClaudeUnexposedPlugins(
    IReadOnlyList<ClaudeInitPlugin> RegisteredWithoutSkills,
    IReadOnlyList<string> PassedWithoutRegistration);
