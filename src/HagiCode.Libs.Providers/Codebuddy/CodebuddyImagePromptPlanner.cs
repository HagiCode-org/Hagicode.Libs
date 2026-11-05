using System.Text.Json;
using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Codebuddy;

/// <summary>
/// Reads the image capability from the initialize result and decides, per image, whether it is sent as an inline ACP image
/// block or listed in the prompt text. The decision is made for one execution and is never kept in provider state.
/// </summary>
internal static class CodebuddyImagePromptPlanner
{
    // Same wording as the dsh planner and CodexChatImagePreparation.AppendSkippedManifest, so every executor gives the agent the same instruction.
    internal const string ManifestHeader = "Chat image attachments that could not be sent inline are available on the local filesystem:";

    internal const string ConnectionNotAdvertisedReason = "connection did not advertise image input";

    /// <summary>
    /// Reads <c>promptCapabilities.image</c>. The probe showed it under <c>agentCapabilities</c>; a top-level copy is tolerated too.
    /// A missing or non-boolean value is false, and an initialize result without it still succeeds.
    /// </summary>
    public static bool ReadInlineImageCapability(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (initializeResult.TryGetProperty("agentCapabilities", out var agentCapabilities) &&
            TryReadPromptImage(agentCapabilities) is { } nested)
        {
            return nested;
        }

        return TryReadPromptImage(initializeResult) ?? false;
    }

    /// <summary>
    /// Plans the prompt for one execution. With no attachments the text is returned unchanged and nothing is inlined, so the
    /// caller sends the same text-only frame as before.
    /// </summary>
    public static CodebuddyImagePromptPlan Plan(
        string prompt,
        IReadOnlyList<CodebuddyImageAttachment> attachments,
        bool acceptsInlineImages)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count == 0)
        {
            return new CodebuddyImagePromptPlan(prompt, [], []);
        }

        var inlineImages = new List<AcpImageBlock>();
        var deliveries = new List<CodebuddyImageDeliveryItem>(attachments.Count);
        var manifestRows = new List<string>();
        long inlineBytes = 0;

        foreach (var attachment in attachments)
        {
            var byteCount = attachment.Bytes?.LongLength ?? 0;
            var reason = DecideReason(attachment, acceptsInlineImages, inlineImages.Count, inlineBytes);
            if (reason is null)
            {
                inlineImages.Add(new AcpImageBlock(attachment.MediaType.ToLowerInvariant(), Convert.ToBase64String(attachment.Bytes!)));
                inlineBytes += byteCount;
                deliveries.Add(new CodebuddyImageDeliveryItem(attachment.ImageId, attachment.MediaType, byteCount, Inlined: true, Reason: null));
            }
            else
            {
                manifestRows.Add(attachment.ManifestLine);
                deliveries.Add(new CodebuddyImageDeliveryItem(attachment.ImageId, attachment.MediaType, byteCount, Inlined: false, Reason: reason));
            }
        }

        return new CodebuddyImagePromptPlan(AppendManifest(prompt, manifestRows), inlineImages, deliveries);
    }

    private static bool? TryReadPromptImage(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("promptCapabilities", out var promptCapabilities) ||
            promptCapabilities.ValueKind != JsonValueKind.Object ||
            !promptCapabilities.TryGetProperty("image", out var image))
        {
            return null;
        }

        return image.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? DecideReason(
        CodebuddyImageAttachment attachment,
        bool acceptsInlineImages,
        int inlineCount,
        long inlineBytes)
    {
        if (!acceptsInlineImages)
        {
            return ConnectionNotAdvertisedReason;
        }

        if (!AcpImageInputLimits.IsAllowedMediaType(attachment.MediaType))
        {
            return "unsupported media type";
        }

        if (attachment.Bytes is null || attachment.Bytes.Length == 0)
        {
            return "empty image data";
        }

        if (attachment.Bytes.LongLength > AcpImageInputLimits.MaxImageBytes)
        {
            return "image exceeds the 5 MiB limit";
        }

        if (inlineCount >= AcpImageInputLimits.MaxImageCount)
        {
            return "image count limit of 10 reached";
        }

        if (inlineBytes + attachment.Bytes.LongLength > AcpImageInputLimits.MaxTotalBytes)
        {
            return "total image size exceeds the 20 MiB limit";
        }

        return null;
    }

    private static string AppendManifest(string prompt, IReadOnlyList<string> rows)
    {
        if (rows.Count == 0)
        {
            return prompt;
        }

        var manifest = string.Join(Environment.NewLine, new[] { ManifestHeader }.Concat(rows));
        return $"{prompt.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{manifest}";
    }
}

/// <summary>
/// The outcome for one image: inlined, or listed in the prompt with a reason. It carries no bytes.
/// </summary>
internal sealed record CodebuddyImageDeliveryItem(string ImageId, string MediaType, long ByteCount, bool Inlined, string? Reason);

/// <summary>
/// The prompt for one execution: the text with any manifest rows, the inline image blocks in order, and the per-image outcomes.
/// </summary>
internal sealed record CodebuddyImagePromptPlan(
    string Text,
    IReadOnlyList<AcpImageBlock> InlineImages,
    IReadOnlyList<CodebuddyImageDeliveryItem> Deliveries);
