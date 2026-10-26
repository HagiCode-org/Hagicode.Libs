using System.Globalization;
using System.Text.RegularExpressions;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// The memory and CPU limits applied to one agent CLI launch, and every value derived from them.
/// Memory values are whole megabytes, rounded down, and the CPU value is a whole percentage of one core, so every launch path
/// produces identical <c>systemd-run</c> arguments.
/// </summary>
public sealed partial record CliResourceLimits
{
    /// <summary>
    /// The name of the environment variable that standalone hosts use to set the memory limit.
    /// </summary>
    public const string EnvironmentVariableName = "AGENT_MEM_MAX";

    /// <summary>
    /// The name of the environment variable that standalone hosts use to set the CPU limit, as a percentage of one core.
    /// </summary>
    public const string CpuEnvironmentVariableName = "AGENT_CPU_MAX";

    /// <summary>
    /// The <c>TasksMax</c> value of every limited scope.
    /// </summary>
    public const int TasksMax = 2000;

    /// <summary>
    /// The largest CPU percentage accepted from <c>AGENT_CPU_MAX</c>; larger values are clamped to it.
    /// </summary>
    public const int MaxCpuPercent = 10000;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliResourceLimits" /> record with a memory limit only.
    /// </summary>
    /// <param name="memoryMaxMegabytes">The hard memory cap in megabytes. Must be positive.</param>
    public CliResourceLimits(int memoryMaxMegabytes)
        : this(memoryMaxMegabytes, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CliResourceLimits" /> record. At least one part must be present.
    /// </summary>
    /// <param name="memoryMaxMegabytes">The hard memory cap in megabytes, or <see langword="null" /> for no memory limit. Must be positive when present.</param>
    /// <param name="cpuPercent">The CPU limit as a percentage of one core (100 is one core, 200 is two), or <see langword="null" /> for no CPU limit. Must be positive when present.</param>
    public CliResourceLimits(int? memoryMaxMegabytes, int? cpuPercent)
    {
        if (memoryMaxMegabytes is null && cpuPercent is null)
        {
            throw new ArgumentException("A resource limit needs a memory size, a CPU percentage or both.");
        }

        if (memoryMaxMegabytes is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryMaxMegabytes.Value);
        }

        if (cpuPercent is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuPercent.Value);
        }

        MemoryMaxMegabytes = memoryMaxMegabytes;
        CpuPercent = cpuPercent;
    }

    /// <summary>
    /// Gets the hard memory cap (<c>MemoryMax</c>) in megabytes, or <see langword="null" /> when no memory limit is set.
    /// </summary>
    public int? MemoryMaxMegabytes { get; }

    /// <summary>
    /// Gets the CPU limit as a percentage of one core (<c>CPUQuota</c>), or <see langword="null" /> when no CPU limit is set.
    /// </summary>
    public int? CpuPercent { get; }

    /// <summary>
    /// Gets the soft limit (<c>MemoryHigh</c>): 85% of the cap, rounded down, or <see langword="null" /> without a memory limit.
    /// </summary>
    public int? MemoryHighMegabytes => MemoryMaxMegabytes is { } maxMegabytes ? (int)(maxMegabytes * 85L / 100) : null;

    /// <summary>
    /// Gets the Node.js heap size for <c>--max-old-space-size</c>: 75% of the cap, rounded down, or <see langword="null" /> without a memory limit.
    /// </summary>
    public int? NodeHeapMegabytes => MemoryMaxMegabytes is { } maxMegabytes ? (int)(maxMegabytes * 75L / 100) : null;

    /// <summary>
    /// Gets the Node.js option that caps the heap, for example <c>--max-old-space-size=3072</c>, or <see langword="null" /> without a memory limit.
    /// </summary>
    public string? NodeHeapOption => NodeHeapMegabytes is { } heapMegabytes
        ? $"--max-old-space-size={heapMegabytes.ToString(CultureInfo.InvariantCulture)}"
        : null;

    /// <summary>
    /// Gets the memory scope properties, in order: <c>MemoryMax</c>, <c>MemoryHigh</c>, <c>MemorySwapMax</c> and <c>TasksMax</c>.
    /// The list is empty without a memory limit.
    /// </summary>
    public IReadOnlyList<string> MemorySystemdRunProperties =>
        MemoryMaxMegabytes is { } maxMegabytes
            ?
            [
                $"MemoryMax={maxMegabytes.ToString(CultureInfo.InvariantCulture)}M",
                $"MemoryHigh={MemoryHighMegabytes!.Value.ToString(CultureInfo.InvariantCulture)}M",
                "MemorySwapMax=0",
                $"TasksMax={TasksMax.ToString(CultureInfo.InvariantCulture)}"
            ]
            : [];

    /// <summary>
    /// Gets the CPU scope properties: <c>CPUQuota</c> alone. The list is empty without a CPU limit.
    /// </summary>
    public IReadOnlyList<string> CpuSystemdRunProperties =>
        CpuPercent is { } percent
            ? [$"CPUQuota={percent.ToString(CultureInfo.InvariantCulture)}%"]
            : [];

    /// <summary>
    /// Gets every scope property, in order: the memory properties, then the CPU properties.
    /// </summary>
    public IReadOnlyList<string> SystemdRunProperties => [.. MemorySystemdRunProperties, .. CpuSystemdRunProperties];

    /// <summary>
    /// Gets the <c>systemd-run</c> arguments that precede the CLI path, ending with <c>--</c>.
    /// </summary>
    public IReadOnlyList<string> SystemdRunArguments
    {
        get
        {
            var arguments = new List<string> { "--user", "--scope", "--quiet" };
            foreach (var property in SystemdRunProperties)
            {
                arguments.Add("-p");
                arguments.Add(property);
            }

            arguments.Add("--");
            return arguments;
        }
    }

    /// <summary>
    /// Parses a size such as <c>4096</c>, <c>4096M</c>, <c>4096MB</c>, <c>4G</c> or <c>4GB</c> (case-insensitive, default unit megabytes).
    /// </summary>
    /// <param name="value">The raw value, for example the content of <c>AGENT_MEM_MAX</c>.</param>
    /// <param name="limits">The parsed memory-only limit, or <see langword="null" /> when the value is empty, zero, negative or malformed.</param>
    /// <returns>
    /// <see langword="false" /> only when the value is malformed, so the caller can warn; an empty, zero or negative value
    /// returns <see langword="true" /> with no limit.
    /// </returns>
    public static bool TryParseSize(string? value, out CliResourceLimits? limits)
    {
        limits = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var match = SizePattern().Match(value.Trim());
        if (!match.Success
            || !long.TryParse(match.Groups["amount"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        if (amount <= 0)
        {
            return true;
        }

        var unit = match.Groups["unit"].Value;
        var megabytes = unit.StartsWith('G') || unit.StartsWith('g') ? amount * 1024 : amount;
        if (megabytes > int.MaxValue)
        {
            return false;
        }

        limits = new CliResourceLimits((int)megabytes);
        return true;
    }

    /// <summary>
    /// Parses a CPU percentage such as <c>200%</c>, <c>200</c> or <c>150</c> (one core is 100). Values above
    /// <see cref="MaxCpuPercent" /> are clamped to it.
    /// </summary>
    /// <param name="value">The raw value, for example the content of <c>AGENT_CPU_MAX</c>.</param>
    /// <param name="cpuPercent">The parsed percentage, or <see langword="null" /> when the value is empty, zero, negative or malformed.</param>
    /// <returns>
    /// <see langword="false" /> only when the value is malformed, so the caller can warn; an empty, zero or negative value
    /// returns <see langword="true" /> with no limit.
    /// </returns>
    public static bool TryParseCpuPercent(string? value, out int? cpuPercent)
    {
        cpuPercent = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var match = CpuPercentPattern().Match(value.Trim());
        if (!match.Success
            || !long.TryParse(match.Groups["amount"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        if (amount <= 0)
        {
            return true;
        }

        cpuPercent = (int)Math.Min(amount, MaxCpuPercent);
        return true;
    }

    [GeneratedRegex(@"^(?<amount>[+-]?\d{1,12})\s*(?<unit>MB|M|GB|G)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^(?<amount>[+-]?\d{1,12})\s*(?<percent>%)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CpuPercentPattern();
}
