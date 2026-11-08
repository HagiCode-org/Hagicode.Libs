using System.Text.Json;
using System.Text.Json.Serialization;

namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// Selects a Copilot Fusion effort tier.
/// </summary>
[JsonConverter(typeof(CopilotFusionTierJsonConverter))]
public enum CopilotFusionTier
{
    /// <summary>
    /// The standard effort tier.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// The high effort tier.
    /// </summary>
    High = 2,

    /// <summary>
    /// The maximum effort tier.
    /// </summary>
    Max = 3
}

/// <summary>
/// Converts Fusion tiers to and from their lowercase wire values.
/// </summary>
public sealed class CopilotFusionTierJsonConverter : JsonConverter<CopilotFusionTier>
{
    public static bool TryParseWireValue(string? value, out CopilotFusionTier tier)
    {
        tier = value switch
        {
            "standard" => CopilotFusionTier.Standard,
            "high" => CopilotFusionTier.High,
            "max" => CopilotFusionTier.Max,
            _ => default
        };
        return value is "standard" or "high" or "max";
    }

    public static string ToWireValue(CopilotFusionTier value)
    {
        return value switch
        {
            CopilotFusionTier.Standard => "standard",
            CopilotFusionTier.High => "high",
            CopilotFusionTier.Max => "max",
            _ => throw new JsonException("Fusion tier must be one of standard, high, or max.")
        };
    }

    public override CopilotFusionTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Fusion tier must be a string: standard, high, or max.");
        }

        return TryParseWireValue(reader.GetString(), out var tier)
            ? tier
            : throw new JsonException("Fusion tier must be one of standard, high, or max.");
    }

    public override void Write(Utf8JsonWriter writer, CopilotFusionTier value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(ToWireValue(value));
    }
}
