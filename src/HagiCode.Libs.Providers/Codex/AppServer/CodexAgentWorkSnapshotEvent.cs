using HagiCode.Libs.Core.AgentWork;
using ManagedCode.CodexSharpSDK.Models;

namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// The event that carries the agent work a Codex run is waiting on. It is written into the turn's event stream at the position
/// where the change was observed, so the caller can show it in order with the agent's output. It carries no raw provider JSON.
/// </summary>
/// <param name="Snapshot">The complete list of subagent items at the moment of the change.</param>
public sealed record CodexAgentWorkSnapshotEvent(AgentWorkSnapshot Snapshot)
    : ThreadEvent(EventType)
{
    /// <summary>The <see cref="ThreadEvent.Type"/> of an agent work snapshot event.</summary>
    public const string EventType = "agent_work_snapshot";
}
