using System.Globalization;

namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// The fields of one <c>/proc/&lt;pid&gt;/stat</c> line that the snapshot needs.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="Name">The command name from the parentheses, which may contain spaces and parentheses.</param>
/// <param name="State">The state letter, such as <c>R</c>, <c>S</c> or <c>Z</c>.</param>
/// <param name="ParentPid">The parent process id.</param>
/// <param name="UserTicks">Utime in clock ticks.</param>
/// <param name="SystemTicks">Stime in clock ticks.</param>
/// <param name="StartTicks">Starttime in clock ticks since boot.</param>
/// <param name="RssPages">Resident pages.</param>
internal sealed record LinuxProcStat(
    int Pid,
    string Name,
    char State,
    int ParentPid,
    long UserTicks,
    long SystemTicks,
    long StartTicks,
    long RssPages);

/// <summary>
/// Parses <c>/proc/&lt;pid&gt;/stat</c>. The command name is found between the first <c>(</c> and the last <c>)</c>, so names with spaces or parentheses parse correctly.
/// </summary>
internal static class LinuxProcStatParser
{
    // Field indexes below are counted after the closing parenthesis, which holds fields 3 onward of proc(5).
    private const int StateIndex = 0;
    private const int ParentPidIndex = 1;
    private const int UserTimeIndex = 11;
    private const int SystemTimeIndex = 12;
    private const int StartTimeIndex = 19;
    private const int ResidentPagesIndex = 21;
    private const int MinimumFieldCount = 22;

    /// <summary>
    /// Attempts to parse one stat line.
    /// </summary>
    /// <param name="text">The full contents of the stat file.</param>
    /// <param name="stat">The parsed fields when the method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when every required field was present and numeric.</returns>
    public static bool TryParse(string text, out LinuxProcStat stat)
    {
        stat = null!;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open <= 0 || close <= open)
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(0, open).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return false;
        }

        var name = text[(open + 1)..close];
        var fields = text[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < MinimumFieldCount)
        {
            return false;
        }

        if (fields[StateIndex].Length != 1
            || !int.TryParse(fields[ParentPidIndex], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parentPid)
            || !long.TryParse(fields[UserTimeIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var userTicks)
            || !long.TryParse(fields[SystemTimeIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var systemTicks)
            || !long.TryParse(fields[StartTimeIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks)
            || !long.TryParse(fields[ResidentPagesIndex], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rssPages))
        {
            return false;
        }

        stat = new LinuxProcStat(pid, name, fields[StateIndex][0], parentPid, userTicks, systemTicks, startTicks, rssPages);
        return true;
    }
}
