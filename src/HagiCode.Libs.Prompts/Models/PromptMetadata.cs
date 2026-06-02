using System.Text.Json.Serialization;

namespace HagiCode.Libs.Prompts.Models;

/// <summary>
/// Represents prompt metadata loaded from a JSON sidecar file.
/// </summary>
public sealed class PromptMetadata
{
    public string Scenario { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string Version { get; set; } = "2.0.0";

    public string? TemplateRef { get; set; }

    public string Syntax { get; set; } = "handlebars";

    public string? SyntaxVersion { get; set; } = "1.0";

    public List<PromptParameter> Parameters { get; set; } = [];

    public string? LastModified { get; set; }

    public string? Author { get; set; }

    public string? Description { get; set; }

    public string? Icon { get; set; }

    public PromptImageDefinition? Image { get; set; }

    public PromptDungeonConfiguration? Dungeon { get; set; }

    public List<string> Tags { get; set; } = [];

    public string? EmbeddedCommand { get; set; }

    [JsonIgnore]
    public PromptSource Source { get; set; } = PromptSource.Default;

    [JsonIgnore]
    public DateTimeOffset? LastModifiedAt { get; set; }
}

/// <summary>
/// Describes a prompt parameter.
/// </summary>
public sealed class PromptParameter
{
    public string Name { get; set; } = string.Empty;

    public string? Type { get; set; }

    public bool Required { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Represents author-time image metadata for a prompt.
/// </summary>
public sealed class PromptImageDefinition
{
    public string? Prompt { get; set; }

    public string? Alt { get; set; }

    public string? AspectRatio { get; set; }

    public string? StageStyleKey { get; set; }

    public string? StageStyleLabel { get; set; }

    public string? ArtStyle { get; set; }

    public string? DisplayMode { get; set; }

    public string? PromptDirection { get; set; }

    public List<string> StyleTags { get; set; } = [];
}

/// <summary>
/// Represents prompt-owned dungeon catalog metadata.
/// </summary>
public sealed class PromptDungeonConfiguration
{
    public bool Enabled { get; set; }

    public string? ScriptKey { get; set; }

    public string? GroupKey { get; set; }

    public int? SortOrder { get; set; }

    public string? DisplayNameKey { get; set; }

    public string? DescriptionKey { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }
}
