using System.Text.Json;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Reads every recorded CodeBuddy steer fixture, so a fixture that is not copied to the output or is not valid JSON Lines fails here.
public sealed class CodebuddySteerCaptureTests
{
    [Test]
    public void Every_codebuddy_steer_fixture_is_copied_to_the_output_and_parses_as_json_lines()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codebuddy");
        Directory.Exists(directory).ShouldBeTrue();

        var files = Directory.GetFiles(directory);
        files.Length.ShouldBe(7);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            lines.ShouldNotBeEmpty(file);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
            }
        }
    }
}
