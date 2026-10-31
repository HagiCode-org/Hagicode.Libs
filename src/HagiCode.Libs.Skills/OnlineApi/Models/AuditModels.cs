using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HagiCode.Libs.Skills.OnlineApi.Models;

public sealed class AuditMetadataRequest
{
    public string Source { get; init; } = string.Empty;

    public List<string> Skills { get; init; } = [];
}

public sealed class AuditMetadataResponse
{
    /// <summary>
    /// Skill slug to provider key (<c>ath</c>, <c>socket</c>, <c>snyk</c>) to that provider's metadata.
    /// Both levels match keys case-insensitively. Provider keys are kept as received.
    /// </summary>
    public Dictionary<string, Dictionary<string, AuditSkillMetadata>> Skills { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AuditSkillMetadata
{
    /// <summary>
    /// Risk value as supplied. Null when the field is absent. Unknown values are kept as received.
    /// </summary>
    [JsonPropertyName("risk")]
    public string? Risk { get; init; }

    [JsonPropertyName("alerts")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int? Alerts { get; init; }

    [JsonPropertyName("score")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int? Score { get; init; }

    /// <summary>
    /// Analysis time exactly as supplied. Null when the field is absent.
    /// </summary>
    [JsonPropertyName("analyzedAt")]
    public string? AnalyzedAt { get; init; }
}

/// <summary>
/// Reads integers leniently: JSON integers, whole-number doubles, and numeric strings are accepted.
/// Anything else (fractions, non-numeric text, booleans, objects, arrays) becomes null instead of failing the parse.
/// </summary>
internal sealed class LenientInt32Converter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var integer))
                {
                    return integer;
                }

                return reader.TryGetDouble(out var real) ? ToWholeInt32(real) : null;
            case JsonTokenType.String:
                return ParseText(reader.GetString());
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value.Value);
    }

    private static int? ParseText(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            ? ToWholeInt32(real)
            : null;
    }

    private static int? ToWholeInt32(double value)
    {
        if (double.IsNaN(value) || Math.Truncate(value) != value || value < int.MinValue || value > int.MaxValue)
        {
            return null;
        }

        return (int)value;
    }
}
