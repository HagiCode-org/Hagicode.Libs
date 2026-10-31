using System.Text.Json;
using HagiCode.Libs.Skills.OnlineApi.Models;
using Shouldly;

namespace HagiCode.Libs.Skills.Tests;

public sealed class AuditSkillMetadataTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Test]
    public void Full_entry_keeps_all_four_values()
    {
        var metadata = Parse("""{"risk":"high","alerts":3,"score":62,"analyzedAt":"2026-10-30T00:00:00.000Z"}""");

        metadata.Risk.ShouldBe("high");
        metadata.Alerts.ShouldBe(3);
        metadata.Score.ShouldBe(62);
        metadata.AnalyzedAt.ShouldBe("2026-10-30T00:00:00.000Z");
    }

    [Test]
    public void Missing_fields_are_absent_not_zero_or_empty()
    {
        var metadata = Parse("""{"risk":"safe"}""");

        metadata.Risk.ShouldBe("safe");
        metadata.Alerts.ShouldBeNull();
        metadata.Score.ShouldBeNull();
        metadata.AnalyzedAt.ShouldBeNull();
    }

    [Test]
    public void Null_fields_are_absent()
    {
        var metadata = Parse("""{"risk":null,"alerts":null,"score":null,"analyzedAt":null}""");

        metadata.Risk.ShouldBeNull();
        metadata.Alerts.ShouldBeNull();
        metadata.Score.ShouldBeNull();
        metadata.AnalyzedAt.ShouldBeNull();
    }

    [Test]
    public void Unknown_fields_are_ignored()
    {
        var metadata = Parse("""{"risk":"low","vendorNote":{"nested":[1,2]},"alerts":1}""");

        metadata.Risk.ShouldBe("low");
        metadata.Alerts.ShouldBe(1);
    }

    [Test]
    public void Non_numeric_score_becomes_absent()
    {
        var metadata = Parse("""{"score":"n/a","alerts":"many"}""");

        metadata.Score.ShouldBeNull();
        metadata.Alerts.ShouldBeNull();
    }

    [Test]
    public void Numeric_text_and_whole_doubles_are_read_as_integers()
    {
        var metadata = Parse("""{"score":" 42 ","alerts":7.0}""");

        metadata.Score.ShouldBe(42);
        metadata.Alerts.ShouldBe(7);
    }

    [Test]
    public void Fractional_numbers_become_absent()
    {
        var metadata = Parse("""{"score":62.5}""");

        metadata.Score.ShouldBeNull();
    }

    private static AuditSkillMetadata Parse(string json) =>
        JsonSerializer.Deserialize<AuditSkillMetadata>(json, Options)!;
}
