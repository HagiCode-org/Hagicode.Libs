using System.Globalization;
using System.Text.Json;

namespace HagiCode.Libs.Core.Transport;

/// <summary>
/// Represents a single CLI message exchanged over the transport.
/// </summary>
/// <param name="Type">The top-level message type.</param>
/// <param name="Content">The full JSON payload for the message.</param>
public sealed record CliMessage(string Type, JsonElement Content)
{
    private static readonly string[] PayloadTimestampProperties = ["event_timestamp", "timestamp"];

    private readonly DateTimeOffset _eventTimestamp = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the instant the message content occurred, always expressed in UTC.
    /// It is the payload time when the provider supplied one, otherwise the instant the message was read.
    /// It is set once and must be carried unchanged by every downstream representation.
    /// </summary>
    public DateTimeOffset EventTimestamp
    {
        get => _eventTimestamp;
        init => _eventTimestamp = value.ToUniversalTime();
    }

    /// <summary>
    /// Reads a valid event time from the payload fields <c>event_timestamp</c> or <c>timestamp</c>.
    /// </summary>
    /// <param name="content">The full JSON payload.</param>
    /// <param name="timestamp">The parsed UTC time when the payload carries a valid one.</param>
    /// <returns><see langword="true"/> when a non-default time was found.</returns>
    public static bool TryReadPayloadTimestamp(JsonElement content, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in PayloadTimestampProperties)
        {
            if (!content.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (DateTimeOffset.TryParse(
                    element.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                && parsed.UtcDateTime != default)
            {
                timestamp = parsed.ToUniversalTime();
                return true;
            }
        }

        return false;
    }
}
