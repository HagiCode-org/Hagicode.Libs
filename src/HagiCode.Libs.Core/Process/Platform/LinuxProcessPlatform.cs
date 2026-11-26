using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// Reads processes from the Linux process filesystem. Start times are computed the same way as <see cref="System.Diagnostics.Process.StartTime" />, so identities agree across the library.
/// </summary>
internal sealed class LinuxProcessPlatform : IProcessPlatform
{
    private const string DefaultProcRoot = "/proc";
    private const string DefaultCgroupRoot = "/sys/fs/cgroup";
    private const int SysconfClockTicks = 2;
    private const int SysconfPageSize = 30;
    private const long FallbackClockTicks = 100;
    private const long FallbackPageSize = 4096;

    private readonly string _procRoot;
    private readonly string _cgroupRoot;
    private readonly Lazy<DateTimeOffset?> _bootTimeUtc;
    private readonly Lazy<long> _clockTicksPerSecond;
    private readonly Lazy<long> _pageSizeBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinuxProcessPlatform" /> class for the running system.
    /// </summary>
    public LinuxProcessPlatform()
        : this(DefaultProcRoot, DefaultCgroupRoot)
    {
    }

    /// <summary>
    /// Initializes a new instance that reads from the given roots. Tests use this to read a fake process filesystem.
    /// </summary>
    /// <param name="procRoot">The process filesystem root, normally <c>/proc</c>.</param>
    /// <param name="cgroupRoot">The cgroup v2 mount, normally <c>/sys/fs/cgroup</c>.</param>
    internal LinuxProcessPlatform(string procRoot, string cgroupRoot)
    {
        _procRoot = procRoot;
        _cgroupRoot = cgroupRoot;
        _bootTimeUtc = new Lazy<DateTimeOffset?>(ReadBootTimeUtc);
        _clockTicksPerSecond = new Lazy<long>(() => ReadSysconf(SysconfClockTicks, FallbackClockTicks));
        _pageSizeBytes = new Lazy<long>(() => ReadSysconf(SysconfPageSize, FallbackPageSize));
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessTableRow> ReadProcessTable()
    {
        var rows = new List<ProcessTableRow>();
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_procRoot);
        }
        catch (IOException)
        {
            return rows;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, out var pid))
            {
                continue;
            }

            if (TryReadStat(pid, out var stat))
            {
                rows.Add(new ProcessTableRow(stat.Pid, stat.ParentPid, stat.Name));
            }
        }

        return rows;
    }

    /// <inheritdoc />
    public ProcessDetails? ReadDetails(int pid, bool includeCommandLine)
    {
        string statText;
        try
        {
            statText = File.ReadAllText(StatPath(pid));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new ProcessDetails(null, null, null, null, null, ProcessAccessibility.Denied);
        }

        if (!LinuxProcStatParser.TryParse(statText, out var stat) || stat.State is 'Z' or 'X')
        {
            return null;
        }

        var bootTime = _bootTimeUtc.Value;
        DateTimeOffset? startedAtUtc = bootTime is { } boot
            ? boot.AddSeconds((double)stat.StartTicks / _clockTicksPerSecond.Value)
            : null;
        if (startedAtUtc is null)
        {
            return new ProcessDetails(null, null, null, null, stat.Name, ProcessAccessibility.Denied);
        }

        var cpuTime = TimeSpan.FromSeconds((double)(stat.UserTicks + stat.SystemTicks) / _clockTicksPerSecond.Value);
        var memoryBytes = stat.RssPages * _pageSizeBytes.Value;

        var accessibility = ProcessAccessibility.Accessible;
        string? commandLine = null;
        if (includeCommandLine)
        {
            commandLine = TryReadCommandLine(pid);
            if (commandLine is null)
            {
                accessibility = ProcessAccessibility.Partial;
            }
        }

        return new ProcessDetails(startedAtUtc, cpuTime, memoryBytes, commandLine, stat.Name, accessibility);
    }

    /// <inheritdoc />
    public IReadOnlyList<int> ReadContainmentMembers(int rootPid)
    {
        var key = ReadContainmentKey(rootPid);
        return key is null ? [] : ReadContainmentMembersByKey(key);
    }

    /// <inheritdoc />
    public string? ReadContainmentKey(int rootPid)
    {
        var rootCgroup = LinuxCgroupMembership.ParseUnifiedPath(ReadTextOrEmpty(Path.Combine(_procRoot, rootPid.ToString(), "cgroup")));
        var ownCgroup = LinuxCgroupMembership.ParseUnifiedPath(ReadTextOrEmpty(Path.Combine(_procRoot, "self", "cgroup")));
        return LinuxCgroupMembership.MayExpand(rootCgroup, ownCgroup) ? rootCgroup : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<int> ReadContainmentMembersByKey(string containmentKey)
    {
        var procsPath = Path.Combine(_cgroupRoot, containmentKey.TrimStart('/'), "cgroup.procs");
        var members = new List<int>();
        foreach (var line in ReadTextOrEmpty(procsPath).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line, out var memberPid))
            {
                members.Add(memberPid);
            }
        }

        return members;
    }

    /// <inheritdoc />
    public ProcessKillResult Kill(int pid) => PosixProcessControl.Kill(pid);

    private string StatPath(int pid) => Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat");

    private bool TryReadStat(int pid, out LinuxProcStat stat)
    {
        stat = null!;
        try
        {
            return LinuxProcStatParser.TryParse(File.ReadAllText(StatPath(pid)), out stat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? TryReadCommandLine(int pid)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "cmdline"));
            if (bytes.Length == 0)
            {
                return null;
            }

            var parts = Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : string.Join(' ', parts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private DateTimeOffset? ReadBootTimeUtc()
    {
        foreach (var line in ReadTextOrEmpty(Path.Combine(_procRoot, "stat")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("btime ", StringComparison.Ordinal)
                && long.TryParse(line["btime ".Length..].Trim(), out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static long ReadSysconf(int name, long fallback)
    {
        if (!OperatingSystem.IsLinux())
        {
            return fallback;
        }

        try
        {
            var value = sysconf(name);
            return value > 0 ? value : fallback;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return fallback;
        }
    }

    [DllImport("libc", EntryPoint = "sysconf")]
    private static extern long sysconf(int name);
}
