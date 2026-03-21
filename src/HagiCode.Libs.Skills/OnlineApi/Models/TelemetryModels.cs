namespace HagiCode.Libs.Skills.OnlineApi.Models;

public abstract record TelemetryEventRequest(string EventName)
{
    internal abstract IEnumerable<KeyValuePair<string, string>> GetParameters();
}

public sealed record InstallTelemetryEventRequest(
    string Source,
    string Skills,
    string Agents,
    bool Global = false,
    string? SkillFilesJson = null,
    string? SourceType = null)
    : TelemetryEventRequest("install")
{
    internal override IEnumerable<KeyValuePair<string, string>> GetParameters()
    {
        yield return new KeyValuePair<string, string>("event", EventName);
        yield return new KeyValuePair<string, string>("source", Source);
        yield return new KeyValuePair<string, string>("skills", Skills);
        yield return new KeyValuePair<string, string>("agents", Agents);
        if (Global)
        {
            yield return new KeyValuePair<string, string>("global", "1");
        }

        if (!string.IsNullOrWhiteSpace(SkillFilesJson))
        {
            yield return new KeyValuePair<string, string>("skillFiles", SkillFilesJson);
        }

        if (!string.IsNullOrWhiteSpace(SourceType))
        {
            yield return new KeyValuePair<string, string>("sourceType", SourceType);
        }
    }
}

public sealed record RemoveTelemetryEventRequest(
    string Skills,
    string Agents,
    string? Source = null,
    bool Global = false,
    string? SourceType = null)
    : TelemetryEventRequest("remove")
{
    internal override IEnumerable<KeyValuePair<string, string>> GetParameters()
    {
        yield return new KeyValuePair<string, string>("event", EventName);
        yield return new KeyValuePair<string, string>("skills", Skills);
        yield return new KeyValuePair<string, string>("agents", Agents);
        if (!string.IsNullOrWhiteSpace(Source))
        {
            yield return new KeyValuePair<string, string>("source", Source);
        }

        if (Global)
        {
            yield return new KeyValuePair<string, string>("global", "1");
        }

        if (!string.IsNullOrWhiteSpace(SourceType))
        {
            yield return new KeyValuePair<string, string>("sourceType", SourceType);
        }
    }
}

public sealed record CheckTelemetryEventRequest(string SkillCount, string UpdatesAvailable)
    : TelemetryEventRequest("check")
{
    internal override IEnumerable<KeyValuePair<string, string>> GetParameters()
    {
        yield return new KeyValuePair<string, string>("event", EventName);
        yield return new KeyValuePair<string, string>("skillCount", SkillCount);
        yield return new KeyValuePair<string, string>("updatesAvailable", UpdatesAvailable);
    }
}

public sealed record UpdateTelemetryEventRequest(string SkillCount, string SuccessCount, string FailCount)
    : TelemetryEventRequest("update")
{
    internal override IEnumerable<KeyValuePair<string, string>> GetParameters()
    {
        yield return new KeyValuePair<string, string>("event", EventName);
        yield return new KeyValuePair<string, string>("skillCount", SkillCount);
        yield return new KeyValuePair<string, string>("successCount", SuccessCount);
        yield return new KeyValuePair<string, string>("failCount", FailCount);
    }
}

public sealed record FindTelemetryEventRequest(string Query, string ResultCount, bool Interactive = false)
    : TelemetryEventRequest("find")
{
    internal override IEnumerable<KeyValuePair<string, string>> GetParameters()
    {
        yield return new KeyValuePair<string, string>("event", EventName);
        yield return new KeyValuePair<string, string>("query", Query);
        yield return new KeyValuePair<string, string>("resultCount", ResultCount);
        if (Interactive)
        {
            yield return new KeyValuePair<string, string>("interactive", "1");
        }
    }
}

public sealed record ExperimentalSyncTelemetryEventRequest(string SkillCount, string SuccessCount, string Agents)
    : TelemetryEventRequest("experimental_sync")
{
    internal override IEnumerable<KeyValuePair<string, string>> GetParameters()
    {
        yield return new KeyValuePair<string, string>("event", EventName);
        yield return new KeyValuePair<string, string>("skillCount", SkillCount);
        yield return new KeyValuePair<string, string>("successCount", SuccessCount);
        yield return new KeyValuePair<string, string>("agents", Agents);
    }
}

public sealed class TelemetryTrackResult
{
    public bool IsSkipped { get; init; }

    public required Uri RequestUri { get; init; }
}
