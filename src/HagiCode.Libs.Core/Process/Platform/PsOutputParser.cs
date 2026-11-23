using System.Globalization;
using System.Text.RegularExpressions;

namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// The fields of one <c>ps</c> detail line.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="ParentPid">The parent process id.</param>
/// <param name="StartedAtUtc">The start time in UTC.</param>
/// <param name="MemoryBytes">Resident set size in bytes.</param>
/// <param name="CpuTime">Cumulative CPU time.</param>
/// <param name="Name">The command name, which may contain spaces.</param>
internal sealed record PsDetailLine(
    int Pid,
    int ParentPid,
    DateTimeOffset StartedAtUtc,
    long MemoryBytes,
    TimeSpan CpuTime,
    string Name);

/// <summary>
/// Parses the output of <c>ps</c> on macOS. The caller runs <c>ps</c> with <c>LC_ALL=C</c> and <c>TZ=UTC</c>, so <c>lstart</c> is a fixed English form in UTC.
/// Parsing is tolerant per line: a malformed line yields <see langword="null" /> and never fails the whole read.
/// </summary>
internal static partial class PsOutputParser
{
    private const string StartTimeFormat = "ddd MMM d HH:mm:ss yyyy";

    /// <summary>
    /// Parses one line of <c>ps -A -o pid=,ppid=,comm=</c>.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The row, or <see langword="null" /> when the line is malformed.</returns>
    public static ProcessTableRow? ParseTableLine(string line)
    {
        var match = TableLine().Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(match.Groups["ppid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ppid))
        {
            return null;
        }

        var name = match.Groups["name"].Value.Trim();
        return new ProcessTableRow(pid, ppid, name.Length == 0 ? null : name);
    }

    /// <summary>
    /// Parses one line of <c>ps -p &lt;pid&gt; -o pid=,ppid=,lstart=,rss=,time=,comm=</c>.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The detail line, or <see langword="null" /> when the line is malformed.</returns>
    public static PsDetailLine? ParseDetailLine(string line)
    {
        var match = DetailLine().Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(match.Groups["ppid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ppid)
            || !TryParseStartTime(match.Groups["lstart"].Value, out var startedAtUtc)
            || !long.TryParse(match.Groups["rss"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rssKibibytes)
            || !TryParseCpuTime(match.Groups["time"].Value, out var cpuTime))
        {
            return null;
        }

        var name = match.Groups["name"].Value.Trim();
        return new PsDetailLine(pid, ppid, startedAtUtc, rssKibibytes * 1024, cpuTime, name);
    }

    /// <summary>
    /// Parses the <c>lstart</c> form, such as <c>Sun Nov  2 10:00:00 2026</c>, as UTC.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="startedAtUtc">The parsed time.</param>
    public static bool TryParseStartTime(string text, out DateTimeOffset startedAtUtc)
    {
        // Collapse the padding that ps adds before single-digit days, so the fixed format matches.
        var normalized = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (DateTime.TryParseExact(normalized, StartTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            startedAtUtc = new DateTimeOffset(parsed, TimeSpan.Zero);
            return true;
        }

        startedAtUtc = default;
        return false;
    }

    /// <summary>
    /// Parses the <c>time</c> form, <c>[[dd-]hh:]mm:ss.ss</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="cpuTime">The parsed duration.</param>
    public static bool TryParseCpuTime(string text, out TimeSpan cpuTime)
    {
        cpuTime = TimeSpan.Zero;
        var days = 0;
        var rest = text.Trim();
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            if (!int.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out days))
            {
                return false;
            }

            rest = rest[(dash + 1)..];
        }

        var parts = rest.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var hours = 0;
        var minutes = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
        {
            return false;
        }

        if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
        {
            return false;
        }

        cpuTime = TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }

    [GeneratedRegex(@"^\s*(?<pid>\d+)\s+(?<ppid>\d+)\s+(?<name>.*?)\s*$")]
    private static partial Regex TableLine();

    [GeneratedRegex(@"^\s*(?<pid>\d+)\s+(?<ppid>\d+)\s+(?<lstart>\S+\s+\S+\s+\d+\s+\d{1,2}:\d{2}:\d{2}\s+\d{4})\s+(?<rss>\d+)\s+(?<time>\S+)\s+(?<name>.*?)\s*$")]
    private static partial Regex DetailLine();
}
