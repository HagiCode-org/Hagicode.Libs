using System.Text.Json;
using System.Text.Json.Serialization;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Core.AgentWork;

/// <summary>
/// The complete list of agent work of one provider stream at one moment. It is always a full snapshot, never a delta.
/// </summary>
/// <param name="Sequence">
/// A counter local to one provider stream, increasing with every emitted snapshot. It orders and de-duplicates snapshots
/// inside that stream only; the authoritative revision of a session is assigned by the host.
/// </param>
/// <param name="Items">The items in start order.</param>
public sealed record AgentWorkSnapshot(long Sequence, IReadOnlyList<AgentWorkItem> Items)
{
    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the synthetic message that carries a snapshot in a provider stream.
    /// </summary>
    public const string MessageType = "agent_work_snapshot";

    /// <summary>
    /// The serializer settings of the snapshot wire format: camelCase names, no null members.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Wraps the snapshot in the synthetic stream message. The message carries no raw provider JSON.
    /// </summary>
    public CliMessage ToCliMessage()
    {
        return new CliMessage(MessageType, JsonSerializer.SerializeToElement(this, JsonOptions));
    }

    /// <summary>
    /// Reads a snapshot back from a synthetic stream message.
    /// </summary>
    /// <returns><see langword="false" /> when the message is not a snapshot message or its content is not valid.</returns>
    public static bool TryFromCliMessage(CliMessage message, out AgentWorkSnapshot snapshot)
    {
        snapshot = null!;
        if (!string.Equals(message.Type, MessageType, StringComparison.Ordinal)
            || message.Content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            snapshot = message.Content.Deserialize<AgentWorkSnapshot>(JsonOptions)!;
            return snapshot is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
