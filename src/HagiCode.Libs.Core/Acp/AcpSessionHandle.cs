using System.Text.Json;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes a started or resumed ACP session.
/// </summary>
/// <param name="SessionId">The bound session identifier.</param>
/// <param name="IsResumed">Whether the session was resumed from a supplied identifier.</param>
/// <param name="RawResponse">The raw JSON result returned by the ACP agent.</param>
public sealed record AcpSessionHandle(string SessionId, bool IsResumed, JsonElement RawResponse);
