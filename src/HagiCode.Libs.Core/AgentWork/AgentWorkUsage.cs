namespace HagiCode.Libs.Core.AgentWork;

/// <summary>
/// Resource usage a provider reported for one unit of agent work. Every field is optional because providers report
/// different subsets.
/// </summary>
/// <param name="TotalTokens">Tokens consumed.</param>
/// <param name="ToolUses">Tool calls made.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds.</param>
public sealed record AgentWorkUsage(long? TotalTokens = null, int? ToolUses = null, long? DurationMs = null);
