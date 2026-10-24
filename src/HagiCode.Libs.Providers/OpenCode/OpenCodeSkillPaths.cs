using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Turns <see cref="OpenCodeOptions.SkillPaths" /> into the <c>skills.paths</c> entry of <c>OPENCODE_CONFIG_CONTENT</c>.
/// </summary>
internal static class OpenCodeSkillPaths
{
    internal const string ConfigContentVariable = "OPENCODE_CONFIG_CONTENT";

    /// <summary>
    /// Returns the environment of the runtime to launch: the configured variables, with the skill paths merged into the
    /// inline configuration. Without skill paths, or for an attached runtime, the configured variables are returned as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The existing inline configuration is not a JSON object with an array at <c>skills.paths</c>.</exception>
    internal static IReadOnlyDictionary<string, string?> Apply(OpenCodeOptions options)
    {
        var paths = options.SkillPaths
            .Select(ArgumentValueNormalizer.NormalizeOptionalValue)
            .Where(static path => path is not null)
            .Cast<string>()
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0 || !string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return options.EnvironmentVariables;
        }

        var configured = options.EnvironmentVariables.TryGetValue(ConfigContentVariable, out var value)
            ? value
            : System.Environment.GetEnvironmentVariable(ConfigContentVariable);

        var environment = new Dictionary<string, string?>(options.EnvironmentVariables, StringComparer.Ordinal)
        {
            [ConfigContentVariable] = Merge(configured, paths),
        };
        return environment;
    }

    private static string Merge(string? configured, IReadOnlyList<string> skillPaths)
    {
        try
        {
            var document = string.IsNullOrWhiteSpace(configured)
                ? new JsonObject()
                : JsonNode.Parse(configured) as JsonObject
                  ?? throw new JsonException($"{ConfigContentVariable} must be a JSON object.");
            var skills = document["skills"] switch
            {
                null => new JsonObject(),
                JsonObject existing => existing,
                _ => throw new JsonException($"{ConfigContentVariable} 'skills' must be a JSON object."),
            };
            var paths = skills["paths"] switch
            {
                null => new JsonArray(),
                JsonArray existing => existing,
                _ => throw new JsonException($"{ConfigContentVariable} 'skills.paths' must be a JSON array."),
            };

            foreach (var skillPath in skillPaths)
            {
                if (!paths.Any(entry => entry is JsonValue jsonValue
                                        && jsonValue.TryGetValue<string>(out var existing)
                                        && string.Equals(existing, skillPath, StringComparison.Ordinal)))
                {
                    paths.Add(skillPath);
                }
            }

            skills["paths"] = paths;
            document["skills"] = skills;
            return document.ToJsonString();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"OpenCode SkillPaths cannot be added because the existing {ConfigContentVariable} could not be extended: {exception.Message}",
                exception);
        }
    }
}
