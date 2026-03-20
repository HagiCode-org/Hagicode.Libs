using System.Text.Json;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents one inbound ACP JSON-RPC notification.
/// </summary>
/// <param name="Method">The notification method.</param>
/// <param name="Parameters">The notification parameters.</param>
public sealed record AcpNotification(string Method, JsonElement Parameters);
