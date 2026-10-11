namespace HagiCode.Libs.Core.AgentWork;

/// <summary>
/// Known values of <see cref="AgentWorkItem.Kind" />. The kind is an open string: a producer may emit another value and
/// consumers treat it as a generic item.
/// </summary>
public static class AgentWorkKinds
{
    /// <summary>A subagent the host may wait on.</summary>
    public const string Subagent = "subagent";

    /// <summary>A timed monitor or host-awaited background command.</summary>
    public const string Monitor = "monitor";
}
