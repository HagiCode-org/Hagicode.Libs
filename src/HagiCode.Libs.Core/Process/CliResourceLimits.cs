using System.Globalization;
using System.Text.RegularExpressions;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// The memory limit applied to one agent CLI launch, and every value derived from it.
/// All values are whole megabytes, rounded down, so every launch path produces identical <c>systemd-run</c> arguments.
/// </summary>
public sealed partial record CliResourceLimits
{
    /// <summary>
    /// The name of the environment variable that standalone hosts use to set the limit.
    /// </summary>
    public const string EnvironmentVariableName = "AGENT_MEM_MAX";

    /// <summary>
    /// The <c>TasksMax</c> value of every limited scope.
    /// </summary>
    public const int TasksMax = 2000;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliResourceLimits" /> record.
    /// </summary>
    /// <param name="memoryMaxMegabytes">The hard memory cap in megabytes. Must be positive.</param>
    public CliResourceLimits(int memoryMaxMegabytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryMaxMegabytes);
        MemoryMaxMegabytes = memoryMaxMegabytes;
    }

    /// <summary>
    /// Gets the hard memory cap (<c>MemoryMax</c>) in megabytes.
    /// </summary>
    public int MemoryMaxMegabytes { get; }

    /// <summary>
    /// Gets the soft limit (<c>MemoryHigh</c>): 85% of the cap, rounded down.
    /// </summary>
    public int MemoryHighMegabytes => (int)(MemoryMaxMegabytes * 85L / 100);

    /// <summary>
    /// Gets the Node.js heap size for <c>--max-old-space-size</c>: 75% of the cap, rounded down.
    /// </summary>
    public int NodeHeapMegabytes => (int)(MemoryMaxMegabytes * 75L / 100);

    /// <summary>
    /// Gets the Node.js option that caps the heap, for example <c>--max-old-space-size=3072</c>.
    /// </summary>
    public string NodeHeapOption => $"--max-old-space-size={NodeHeapMegabytes.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Gets the scope properties, in order: <c>MemoryMax</c>, <c>MemoryHigh</c>, <c>MemorySwapMax</c> and <c>TasksMax</c>.
    /// </summary>
    public IReadOnlyList<string> SystemdRunProperties =>
    [
        $"MemoryMax={MemoryMaxMegabytes.ToString(CultureInfo.InvariantCulture)}M",
        $"MemoryHigh={MemoryHighMegabytes.ToString(CultureInfo.InvariantCulture)}M",
        "MemorySwapMax=0",
        $"TasksMax={TasksMax.ToString(CultureInfo.InvariantCulture)}"
    ];

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
    /// <param name="limits">The parsed limit, or <see langword="null" /> when the value is empty, zero, negative or malformed.</param>
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

    [GeneratedRegex(@"^(?<amount>[+-]?\d{1,12})\s*(?<unit>MB|M|GB|G)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}
