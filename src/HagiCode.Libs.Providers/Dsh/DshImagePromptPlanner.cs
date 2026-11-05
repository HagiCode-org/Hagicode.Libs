using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Dsh;

/// <summary>
/// Decides, per image, whether it is inlined as an ACP image block or listed in the prompt text, and builds the text
/// for both the first attempt and the single text-only retry.
/// </summary>
internal static class DshImagePromptPlanner
{
    // Same wording as CodexChatImagePreparation.AppendSkippedManifest, so dsh sees the same instruction on every executor.
    internal const string ManifestHeader = "Chat image attachments that could not be sent inline are available on the local filesystem:";

    // dsh 0.2.0-rc.2 refuses inline images on a connection that did not advertise them. Match the message text,
    // because dsh does not send a dedicated error code for this case.
    private const string InlineNotAdvertisedMessage = "inline image prompts were not advertised";

    public static DshImagePromptPlan Plan(
        string prompt,
        IReadOnlyList<DshImageAttachment> attachments,
        IReadOnlyList<string> extraManifestRows,
        bool connectionSupportsImages)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(extraManifestRows);

        var inlineImages = new List<AcpImageBlock>();
        var deliveries = new List<DshImageDeliveryItem>(attachments.Count);
        var manifestRows = new List<string>(extraManifestRows);
        long inlineBytes = 0;

        foreach (var attachment in attachments)
        {
            var reason = DecideReason(attachment, connectionSupportsImages, inlineImages.Count, inlineBytes);
            var inlined = reason is null;
            deliveries.Add(new DshImageDeliveryItem(
                attachment.ImageId,
                attachment.MediaType,
                attachment.Bytes.LongLength,
                inlined,
                reason));

            if (inlined)
            {
                inlineImages.Add(new AcpImageBlock(attachment.MediaType, Convert.ToBase64String(attachment.Bytes)));
                inlineBytes += attachment.Bytes.LongLength;
            }
            else
            {
                manifestRows.Add(attachment.ManifestLine);
            }
        }

        var fallbackRows = new List<string>(extraManifestRows);
        fallbackRows.AddRange(attachments.Select(static attachment => attachment.ManifestLine));
        var fallbackDeliveries = attachments
            .Select(static attachment => new DshImageDeliveryItem(
                attachment.ImageId,
                attachment.MediaType,
                attachment.Bytes.LongLength,
                Inlined: false,
                Reason: DshImageDeliveryReasons.InlineRejected))
            .ToArray();

        return new DshImagePromptPlan(
            AppendManifest(prompt, manifestRows),
            inlineImages,
            deliveries,
            AppendManifest(prompt, fallbackRows),
            fallbackDeliveries);
    }

    /// <summary>
    /// Whether a prompt failure is dsh's refusal of inline images on this connection.
    /// </summary>
    public static bool IsInlineImageRejection(string errorMessage)
    {
        return errorMessage.Contains("with code -32602", StringComparison.OrdinalIgnoreCase) &&
               errorMessage.Contains(InlineNotAdvertisedMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DecideReason(
        DshImageAttachment attachment,
        bool connectionSupportsImages,
        int inlineCount,
        long inlineBytes)
    {
        if (!connectionSupportsImages)
        {
            return DshImageDeliveryReasons.ConnectionNotAdvertised;
        }

        if (!AcpImageInputLimits.IsAllowedMediaType(attachment.MediaType))
        {
            return DshImageDeliveryReasons.UnsupportedMediaType;
        }

        if (attachment.Bytes.LongLength > AcpImageInputLimits.MaxImageBytes)
        {
            return DshImageDeliveryReasons.ImageTooLarge;
        }

        if (inlineCount >= AcpImageInputLimits.MaxImageCount)
        {
            return DshImageDeliveryReasons.ImageCountLimit;
        }

        if (inlineBytes + attachment.Bytes.LongLength > AcpImageInputLimits.MaxTotalBytes)
        {
            return DshImageDeliveryReasons.TotalSizeLimit;
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
/// Reason codes written to the image delivery result. They carry no image content.
/// </summary>
internal static class DshImageDeliveryReasons
{
    public const string ConnectionNotAdvertised = "connection_not_advertised";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string ImageTooLarge = "image_too_large";
    public const string ImageCountLimit = "image_count_limit";
    public const string TotalSizeLimit = "total_size_limit";
    public const string InlineRejected = "inline_rejected";
}

/// <summary>
/// The outcome for one image: inlined, or listed in the prompt with a reason. It carries no bytes.
/// </summary>
internal sealed record DshImageDeliveryItem(string ImageId, string MediaType, long ByteCount, bool Inlined, string? Reason);

/// <summary>
/// The prompt to send: the first attempt with inline images, and the text-only fallback for a rejected connection.
/// </summary>
internal sealed record DshImagePromptPlan(
    string Text,
    IReadOnlyList<AcpImageBlock> InlineImages,
    IReadOnlyList<DshImageDeliveryItem> Deliveries,
    string FallbackText,
    IReadOnlyList<DshImageDeliveryItem> FallbackDeliveries);
