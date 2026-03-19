using System.Text.Json;

namespace HagiCode.Libs.Core.Transport;

/// <summary>
/// Represents a single CLI message exchanged over the transport.
/// </summary>
/// <param name="Type">The top-level message type.</param>
/// <param name="Content">The full JSON payload for the message.</param>
public sealed record CliMessage(string Type, JsonElement Content);
