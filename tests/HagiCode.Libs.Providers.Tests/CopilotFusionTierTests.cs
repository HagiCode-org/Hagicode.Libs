using System.Text.Json;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CopilotFusionTierTests
{
    [TestCase(CopilotFusionTier.Standard, "\"standard\"")]
    [TestCase(CopilotFusionTier.High, "\"high\"")]
    [TestCase(CopilotFusionTier.Max, "\"max\"")]
    public void Json_round_trips_supported_tiers(CopilotFusionTier tier, string expectedJson)
    {
        var json = JsonSerializer.Serialize(tier);

        json.ShouldBe(expectedJson);
        JsonSerializer.Deserialize<CopilotFusionTier>(json).ShouldBe(tier);
    }

    [Test]
    public void Missing_tier_deserializes_as_unspecified()
    {
        var options = JsonSerializer.Deserialize<CopilotOptions>("{}");

        options.ShouldNotBeNull();
        options.FusionTier.ShouldBeNull();
    }

    [Test]
    public void Copilot_options_round_trip_unspecified_tier_without_inference()
    {
        var json = JsonSerializer.Serialize(new CopilotOptions());
        var options = JsonSerializer.Deserialize<CopilotOptions>(json);

        options.ShouldNotBeNull();
        options.FusionTier.ShouldBeNull();
        options.FusionTierIsExplicitlyUnset.ShouldBeFalse();
    }

    [Test]
    public void Copilot_options_round_trip_explicit_unset_separately_from_omission()
    {
        var json = JsonSerializer.Serialize(new CopilotOptions { FusionTierIsExplicitlyUnset = true });
        var options = JsonSerializer.Deserialize<CopilotOptions>(json);

        options.ShouldNotBeNull();
        options.FusionTier.ShouldBeNull();
        options.FusionTierIsExplicitlyUnset.ShouldBeTrue();
    }

    [TestCase("\"unknown\"")]
    [TestCase("\"High\"")]
    [TestCase("1")]
    [TestCase("{}")]
    public void Json_rejects_unknown_or_malformed_tier_values(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CopilotFusionTier>(json));
    }

    [Test]
    [TestCase(0)]
    [TestCase(42)]
    public void Json_rejects_undefined_numeric_enum_values(int value)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Serialize((CopilotFusionTier)value));
    }
}
