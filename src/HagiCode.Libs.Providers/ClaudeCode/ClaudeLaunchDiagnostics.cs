using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Describes a Claude Code launch for logs. Every helper here is pure: it names flags and environment variables and
/// never prints a credential, a prompt or a message body.
/// </summary>
/// <remarks>
/// The argument description is an allowlist. A flag's value is printed only for the flags in <see cref="ShownValueFlags" />
/// and for a <c>--settings</c> file path; every other value, including the system prompt, the appended system prompt and
/// the MCP configuration, is hidden. A flag added later stays hidden until it is listed here.
/// </remarks>
internal static class ClaudeLaunchDiagnostics
{
    private const string SettingsFlag = "--settings";

    private static readonly IReadOnlySet<string> ShownValueFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--plugin-dir",
        "--add-dir",
        "--model",
        "--permission-mode",
        "--session-id",
        "--resume",
        "--effort",
        "--thinking-display",
        "--debug-file"
    };

    private static readonly string[] CredentialVariables = ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL"];

    // A flag name is a lowercase word with hyphens. Prompt text, JSON and paths never match, so they are never printed as a flag.
    private static readonly Regex FlagNamePattern = new("^--[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Describes the command line: every flag name in order, its value only when the flag is allowlisted, and an inline
    /// <c>--settings</c> JSON value as its length.
    /// </summary>
    /// <param name="arguments">The argument list handed to the CLI.</param>
    /// <returns>The description, with flags separated by single spaces.</returns>
    public static string DescribeArguments(IReadOnlyList<string> arguments)
    {
        var parts = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var flag = arguments[index];
            if (!IsFlagName(flag))
            {
                // A value that no flag printed above claimed. It is never printed.
                continue;
            }

            var hasValue = index + 1 < arguments.Count && !IsFlagName(arguments[index + 1]);
            if (!hasValue)
            {
                parts.Add(flag);
                continue;
            }

            var value = arguments[++index];
            if (string.Equals(flag, SettingsFlag, StringComparison.Ordinal))
            {
                parts.Add(DescribeSettings(value));
            }
            else if (ShownValueFlags.Contains(flag))
            {
                parts.Add($"{flag} {value}");
            }
            else
            {
                parts.Add(flag);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Describes the child environment: the sorted names of the variables that carry a value, and whether each
    /// <c>ANTHROPIC_*</c> credential variable is present. No value is ever read into the output.
    /// </summary>
    /// <param name="environment">The environment handed to the child process. A <see langword="null" /> value removes the variable.</param>
    /// <returns>The description.</returns>
    public static string DescribeEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var names = environment
            .Where(pair => pair.Value is not null && !CredentialVariables.Contains(pair.Key, StringComparer.Ordinal))
            .Select(pair => pair.Key)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var credentials = CredentialVariables.Select(name =>
            $"{name}={(string.IsNullOrEmpty(environment.GetValueOrDefault(name)) ? "absent" : "present")}");

        var namesText = names.Length == 0 ? "(none)" : string.Join(", ", names);
        return $"{namesText}; {string.Join(", ", credentials)}";
    }

    /// <summary>
    /// Describes one stdin message by its type, its subtype and the UTF-8 byte length of its serialized content. For a
    /// control message the subtype is read from its <c>request</c> or <c>response</c> object.
    /// </summary>
    /// <param name="message">The message written to the CLI.</param>
    /// <returns>The description. It never contains message text.</returns>
    public static string DescribeStdinMessage(CliMessage message)
    {
        var byteLength = Encoding.UTF8.GetByteCount(message.Content.GetRawText());
        var subtype = ReadSubtype(message);
        return subtype is null
            ? $"type={message.Type} bytes={byteLength}"
            : $"type={message.Type} subtype={subtype} bytes={byteLength}";
    }

    private static bool IsFlagName(string argument) => FlagNamePattern.IsMatch(argument);

    private static string DescribeSettings(string value)
    {
        return value.TrimStart().StartsWith('{')
            ? $"{SettingsFlag} inline-json({value.Length} chars)"
            : $"{SettingsFlag} {value}";
    }

    private static string? ReadSubtype(CliMessage message)
    {
        if (message.Content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryReadString(message.Content, "subtype") is { } topLevel)
        {
            return topLevel;
        }

        foreach (var envelope in new[] { "request", "response" })
        {
            if (message.Content.TryGetProperty(envelope, out var nested)
                && nested.ValueKind == JsonValueKind.Object
                && TryReadString(nested, "subtype") is { } nestedSubtype)
            {
                return nestedSubtype;
            }
        }

        return null;
    }

    private static string? TryReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
