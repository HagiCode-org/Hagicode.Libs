using System.Text.Json;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// One option offered by an ACP <c>session/request_permission</c> request.
/// </summary>
/// <param name="OptionId">The <c>optionId</c> the client sends back to select the option.</param>
/// <param name="Name">The display name of the option. Empty when the agent sent none.</param>
/// <param name="Kind">The option kind, for example <c>allow_once</c> or <c>reject_once</c>. Empty when the agent sent none.</param>
public sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

/// <summary>
/// An ACP <c>session/request_permission</c> request the agent sent and the client must answer.
/// </summary>
/// <param name="Parameters">The raw <c>params</c> of the request, unchanged.</param>
/// <param name="ToolCallId">The <c>toolCall.toolCallId</c> of the request, or <see langword="null" /> when it has none.</param>
/// <param name="Options">The offered options with an <c>optionId</c>, in the order the agent sent them.</param>
public sealed record AcpPermissionRequest(
    JsonElement Parameters,
    string? ToolCallId,
    IReadOnlyList<AcpPermissionOption> Options)
{
    /// <summary>
    /// Reads a request from the raw <c>params</c> of a <c>session/request_permission</c> request. Options without an
    /// <c>optionId</c> are skipped.
    /// </summary>
    /// <param name="parameters">The raw request parameters.</param>
    /// <returns>The request; a missing or malformed field yields an empty value rather than an error.</returns>
    public static AcpPermissionRequest FromParameters(JsonElement parameters)
    {
        string? toolCallId = null;
        var options = new List<AcpPermissionOption>();
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("toolCall", out var toolCall))
            {
                toolCallId = ReadString(toolCall, "toolCallId");
            }

            if (parameters.TryGetProperty("options", out var optionElements) &&
                optionElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in optionElements.EnumerateArray())
                {
                    if (ReadString(option, "optionId") is not { Length: > 0 } optionId)
                    {
                        continue;
                    }

                    options.Add(new AcpPermissionOption(
                        optionId,
                        ReadString(option, "name") ?? string.Empty,
                        ReadString(option, "kind") ?? string.Empty));
                }
            }
        }

        return new AcpPermissionRequest(parameters.Clone(), toolCallId, options);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

/// <summary>
/// The answer a <see cref="AcpPermissionRequestHandler" /> gives to a <c>session/request_permission</c> request: one selected
/// option, or the <c>cancelled</c> outcome.
/// </summary>
public sealed record AcpPermissionDecision
{
    private AcpPermissionDecision(string? selectedOptionId)
    {
        SelectedOptionId = selectedOptionId;
    }

    /// <summary>
    /// Gets the decision that answers with the <c>cancelled</c> outcome.
    /// </summary>
    public static AcpPermissionDecision Cancelled { get; } = new((string?)null);

    /// <summary>
    /// Gets the selected <c>optionId</c>, or <see langword="null" /> for a <see cref="Cancelled" /> decision.
    /// </summary>
    public string? SelectedOptionId { get; }

    /// <summary>
    /// Gets a value indicating whether the decision answers with the <c>cancelled</c> outcome.
    /// </summary>
    public bool IsCancelled => SelectedOptionId is null;

    /// <summary>
    /// Creates the decision that answers with the <c>selected</c> outcome and <paramref name="optionId" />.
    /// </summary>
    /// <param name="optionId">The <c>optionId</c> to select.</param>
    /// <returns>The decision.</returns>
    public static AcpPermissionDecision Select(string optionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        return new AcpPermissionDecision(optionId);
    }
}

/// <summary>
/// Decides an ACP <c>session/request_permission</c> request before the client's default answer applies.
/// </summary>
/// <param name="request">The request to decide.</param>
/// <param name="cancellationToken">Cancelled when the client is closing.</param>
/// <returns>
/// The decision to answer with, or <see langword="null" /> for no decision, in which case the client answers the request
/// as it does without a handler.
/// </returns>
public delegate Task<AcpPermissionDecision?> AcpPermissionRequestHandler(
    AcpPermissionRequest request,
    CancellationToken cancellationToken);
