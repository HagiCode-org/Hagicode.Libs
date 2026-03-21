namespace HagiCode.Libs.Skills.OnlineApi;

public sealed class OnlineApiOptions
{
    public const string SectionName = "HagiCode:Skills:OnlineApi";

    public Uri SearchBaseUri { get; set; } = new("https://skills.sh/");

    public Uri AuditBaseUri { get; set; } = new("https://add-skill.vercel.sh/");

    public Uri TelemetryBaseUri { get; set; } = new("https://add-skill.vercel.sh/");

    public Uri GitHubBaseUri { get; set; } = new("https://api.github.com/");

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool DisableTelemetry { get; set; }

    public string GitHubUserAgent { get; set; } = "HagiCode.Libs.Skills";

    public string? GitHubToken { get; set; }

    public string? TelemetryVersion { get; set; }

    public bool TelemetryIsCi { get; set; }
}
