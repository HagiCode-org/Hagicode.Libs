namespace HagiCode.Libs.Core.AgentWork;

/// <summary>
/// The values of <see cref="AgentWorkItem.Status" />.
/// </summary>
public static class AgentWorkStatuses
{
    /// <summary>The work was requested but has not reported progress yet.</summary>
    public const string Starting = "starting";

    /// <summary>The work is running.</summary>
    public const string Running = "running";

    /// <summary>The work finished successfully.</summary>
    public const string Completed = "completed";

    /// <summary>The work ended with a failure.</summary>
    public const string Failed = "failed";

    /// <summary>The work was stopped, timed out, or vanished without a result.</summary>
    public const string Stopped = "stopped";

    /// <summary>
    /// Reports whether the status means the work is still going on.
    /// </summary>
    public static bool IsActive(string? status)
    {
        return string.Equals(status, Starting, StringComparison.Ordinal)
               || string.Equals(status, Running, StringComparison.Ordinal);
    }
}
