using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// The default limit provider for standalone hosts. It reads <c>AGENT_MEM_MAX</c> (for example <c>4G</c> or <c>4096M</c>)
/// and <c>AGENT_CPU_MAX</c> (for example <c>200%</c> or <c>200</c>) on every call and combines them into one limit.
/// Each variable is independent: an empty, zero, negative or malformed value means no limit for that resource, and a
/// malformed value is logged once per variable.
/// </summary>
public sealed class EnvironmentCliResourceLimitProvider : ICliResourceLimitProvider
{
    private readonly Func<string?> _readMemoryValue;
    private readonly Func<string?> _readCpuValue;
    private readonly ILogger<EnvironmentCliResourceLimitProvider> _logger;
    private int _memoryMalformedValueLogged;
    private int _cpuMalformedValueLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentCliResourceLimitProvider" /> class.
    /// </summary>
    public EnvironmentCliResourceLimitProvider(ILogger<EnvironmentCliResourceLimitProvider>? logger = null)
        : this(
            static () => System.Environment.GetEnvironmentVariable(CliResourceLimits.EnvironmentVariableName),
            static () => System.Environment.GetEnvironmentVariable(CliResourceLimits.CpuEnvironmentVariableName),
            logger)
    {
    }

    internal EnvironmentCliResourceLimitProvider(Func<string?> readMemoryValue, ILogger<EnvironmentCliResourceLimitProvider>? logger = null)
        : this(readMemoryValue, static () => null, logger)
    {
    }

    internal EnvironmentCliResourceLimitProvider(
        Func<string?> readMemoryValue,
        Func<string?> readCpuValue,
        ILogger<EnvironmentCliResourceLimitProvider>? logger = null)
    {
        _readMemoryValue = readMemoryValue ?? throw new ArgumentNullException(nameof(readMemoryValue));
        _readCpuValue = readCpuValue ?? throw new ArgumentNullException(nameof(readCpuValue));
        _logger = logger ?? NullLogger<EnvironmentCliResourceLimitProvider>.Instance;
    }

    /// <inheritdoc />
    public CliResourceLimits? GetCurrent()
    {
        var memoryValue = _readMemoryValue();
        var cpuValue = _readCpuValue();

        var memoryParsed = CliResourceLimits.TryParseSize(memoryValue, out var memoryLimits);
        if (!memoryParsed && Interlocked.Exchange(ref _memoryMalformedValueLogged, 1) == 0)
        {
            _logger.LogWarning(
                "{VariableName}='{Value}' is not a size such as 4096, 4096M or 4G; agent CLIs run without a memory limit.",
                CliResourceLimits.EnvironmentVariableName,
                memoryValue);
        }

        var cpuParsed = CliResourceLimits.TryParseCpuPercent(cpuValue, out var cpuPercent);
        if (!cpuParsed && Interlocked.Exchange(ref _cpuMalformedValueLogged, 1) == 0)
        {
            _logger.LogWarning(
                "{VariableName}='{Value}' is not a percentage such as 200% or 200; agent CLIs run without a CPU limit.",
                CliResourceLimits.CpuEnvironmentVariableName,
                cpuValue);
        }

        var memoryMegabytes = memoryLimits?.MemoryMaxMegabytes;
        if (memoryMegabytes is null && cpuPercent is null)
        {
            return null;
        }

        return new CliResourceLimits(memoryMegabytes, cpuPercent);
    }
}
