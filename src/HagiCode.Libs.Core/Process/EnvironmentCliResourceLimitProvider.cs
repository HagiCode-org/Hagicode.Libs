using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// The default limit provider for standalone hosts. It reads <c>AGENT_MEM_MAX</c> (for example <c>4G</c> or <c>4096M</c>)
/// on every call; an empty, zero, negative or malformed value means no limit, and a malformed value is logged once.
/// </summary>
public sealed class EnvironmentCliResourceLimitProvider : ICliResourceLimitProvider
{
    private readonly Func<string?> _readValue;
    private readonly ILogger<EnvironmentCliResourceLimitProvider> _logger;
    private int _malformedValueLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentCliResourceLimitProvider" /> class.
    /// </summary>
    public EnvironmentCliResourceLimitProvider(ILogger<EnvironmentCliResourceLimitProvider>? logger = null)
        : this(static () => System.Environment.GetEnvironmentVariable(CliResourceLimits.EnvironmentVariableName), logger)
    {
    }

    internal EnvironmentCliResourceLimitProvider(Func<string?> readValue, ILogger<EnvironmentCliResourceLimitProvider>? logger = null)
    {
        _readValue = readValue ?? throw new ArgumentNullException(nameof(readValue));
        _logger = logger ?? NullLogger<EnvironmentCliResourceLimitProvider>.Instance;
    }

    /// <inheritdoc />
    public CliResourceLimits? GetCurrent()
    {
        var value = _readValue();
        if (CliResourceLimits.TryParseSize(value, out var limits))
        {
            return limits;
        }

        if (Interlocked.Exchange(ref _malformedValueLogged, 1) == 0)
        {
            _logger.LogWarning(
                "{VariableName}='{Value}' is not a size such as 4096, 4096M or 4G; agent CLIs run without a memory limit.",
                CliResourceLimits.EnvironmentVariableName,
                value);
        }

        return null;
    }
}
