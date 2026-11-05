using HagiCode.Libs.Providers.Dsh;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DshImagePromptPlannerTests
{
    private const string ManifestHeader = "Chat image attachments that could not be sent inline are available on the local filesystem:";

    [Test]
    public void Plan_inlines_valid_images_in_order_when_connection_supports_images()
    {
        var plan = DshImagePromptPlanner.Plan(
            "describe",
            [Image("image-1", "image/png", 10), Image("image-2", "image/jpeg", 20), Image("image-3", "image/webp", 30)],
            [],
            connectionSupportsImages: true);

        plan.InlineImages.Select(static block => block.MimeType).ShouldBe(["image/png", "image/jpeg", "image/webp"]);
        plan.Text.ShouldBe("describe");
        plan.Deliveries.Select(static item => item.Inlined).ShouldAllBe(static inlined => inlined);
    }

    [Test]
    public void Plan_lists_every_image_by_path_when_connection_does_not_advertise_images()
    {
        var plan = DshImagePromptPlanner.Plan("describe", [Image("image-1", "image/png", 10)], [], connectionSupportsImages: false);

        plan.InlineImages.ShouldBeEmpty();
        plan.Deliveries.Single().Reason.ShouldBe("connection_not_advertised");
        plan.Text.ShouldStartWith("describe" + Environment.NewLine + Environment.NewLine + ManifestHeader);
        plan.Text.ShouldContain("| path=/tmp/image-1.png");
    }

    [Test]
    public void Plan_rejects_media_types_outside_the_four_allowed_ones()
    {
        var plan = DshImagePromptPlanner.Plan("describe", [Image("image-1", "image/bmp", 10)], [], connectionSupportsImages: true);

        plan.InlineImages.ShouldBeEmpty();
        plan.Deliveries.Single().Reason.ShouldBe("unsupported_media_type");
    }

    [Test]
    public void Plan_lists_an_image_over_the_single_image_limit_by_path()
    {
        var oversized = Image("image-1", "image/png", 5 * 1024 * 1024 + 1);

        var plan = DshImagePromptPlanner.Plan("describe", [oversized], [], connectionSupportsImages: true);

        plan.InlineImages.ShouldBeEmpty();
        plan.Deliveries.Single().Reason.ShouldBe("image_too_large");
    }

    [Test]
    public void Plan_inlines_at_most_ten_images()
    {
        var attachments = Enumerable.Range(1, 11).Select(i => Image($"image-{i}", "image/png", 10)).ToArray();

        var plan = DshImagePromptPlanner.Plan("describe", attachments, [], connectionSupportsImages: true);

        plan.InlineImages.Count.ShouldBe(10);
        plan.Deliveries.Last().Reason.ShouldBe("image_count_limit");
        plan.Text.ShouldContain("| image=image-11 |");
    }

    [Test]
    public void Plan_stops_inlining_once_the_total_would_exceed_twenty_mebibytes()
    {
        const int size = 4_718_592; // 4.5 MiB, under the single-image limit
        var attachments = Enumerable.Range(1, 5).Select(i => Image($"image-{i}", "image/png", size)).ToArray();

        var plan = DshImagePromptPlanner.Plan("describe", attachments, [], connectionSupportsImages: true);

        plan.InlineImages.Count.ShouldBe(4);
        plan.Deliveries.Last().Reason.ShouldBe("total_size_limit");
    }

    [Test]
    public void Plan_keeps_extra_manifest_rows_even_when_no_image_is_attached()
    {
        var plan = DshImagePromptPlanner.Plan("describe", [], ["- lost.png | image/png | session=s | image=x | path=/tmp/lost.png"], connectionSupportsImages: true);

        plan.InlineImages.ShouldBeEmpty();
        plan.Deliveries.ShouldBeEmpty();
        plan.Text.ShouldContain("path=/tmp/lost.png");
    }

    [Test]
    public void Plan_leaves_the_text_untouched_when_there_is_nothing_to_list()
    {
        var plan = DshImagePromptPlanner.Plan("plain prompt", [], [], connectionSupportsImages: false);

        plan.Text.ShouldBe("plain prompt");
        plan.FallbackText.ShouldBe("plain prompt");
    }

    [Test]
    public void Plan_fallback_text_lists_every_image_and_marks_them_as_rejected()
    {
        var plan = DshImagePromptPlanner.Plan("describe", [Image("image-1", "image/png", 10)], [], connectionSupportsImages: true);

        plan.FallbackText.ShouldContain("| image=image-1 |");
        plan.FallbackDeliveries.Single().Reason.ShouldBe("inline_rejected");
        plan.FallbackDeliveries.Single().Inlined.ShouldBeFalse();
    }

    [TestCase("dsh ACP method 'session/prompt' failed with code -32602: Invalid params: inline image prompts were not advertised by this connection", true)]
    [TestCase("ACP method 'session/prompt' failed with code -32602: Invalid params: a prompt is already in flight for this session", false)]
    [TestCase("ACP method 'session/prompt' failed with code -32000: inline image prompts were not advertised by this connection", false)]
    [TestCase("connection reset", false)]
    public void IsInlineImageRejection_matches_only_the_dsh_refusal(string message, bool expected)
    {
        DshImagePromptPlanner.IsInlineImageRejection(message).ShouldBe(expected);
    }

    private static DshImageAttachment Image(string imageId, string mediaType, long size)
    {
        return new DshImageAttachment(
            imageId,
            mediaType,
            new byte[size],
            $"- {imageId}.png | {mediaType} | session=s | image={imageId} | path=/tmp/{imageId}.png");
    }
}
