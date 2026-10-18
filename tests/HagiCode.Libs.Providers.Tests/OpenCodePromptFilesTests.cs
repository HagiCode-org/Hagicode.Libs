using System.Text.Json;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Pins the <c>files</c> member of the OpenCode 2.0 prompt body and keeps image data out of diagnostic text.
/// </summary>
public sealed class OpenCodePromptFilesTests
{
    private const string DataUrl = "data:image/png;base64,iVBORw0KGgo=";

    [Test]
    public void Request_with_an_image_serializes_files_with_mime_uri_and_name_only()
    {
        var request = OpenCodeSessionPromptRequest.FromTextWithFiles(
            "Describe the image.",
            [new OpenCodePromptFile { Mime = "image/png", Uri = DataUrl, Name = "shot.png" }]);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var root = document.RootElement;

        root.GetProperty("text").GetString().ShouldBe("Describe the image.");
        var file = root.GetProperty("files").EnumerateArray().Single();
        file.GetProperty("mime").GetString().ShouldBe("image/png");
        file.GetProperty("uri").GetString().ShouldBe(DataUrl);
        file.GetProperty("name").GetString().ShouldBe("shot.png");
        file.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["mime", "name", "uri"]);
    }

    [Test]
    public void Text_only_request_and_empty_file_list_have_no_files_member()
    {
        using var textOnly = JsonDocument.Parse(JsonSerializer.Serialize(OpenCodeSessionPromptRequest.FromText("hello")));
        var emptyFiles = JsonSerializer.Serialize(OpenCodeSessionPromptRequest.FromTextWithFiles("hello", []));

        textOnly.RootElement.TryGetProperty("files", out _).ShouldBeFalse();
        emptyFiles.ShouldNotContain("files");
    }

    [Test]
    public void Request_and_file_diagnostics_never_contain_the_data_url()
    {
        var file = new OpenCodePromptFile { Mime = "image/png", Uri = DataUrl, Name = "shot.png" };
        var request = OpenCodeSessionPromptRequest.FromTextWithFiles("Describe the image.", [file]);

        request.ToString().ShouldNotContain("base64");
        request.ToString().ShouldNotContain("iVBORw0KGgo");
        file.ToString().ShouldNotContain("base64");
        file.ToString().ShouldNotContain("iVBORw0KGgo");
        file.ToString().ShouldContain("image/png");
    }
}
