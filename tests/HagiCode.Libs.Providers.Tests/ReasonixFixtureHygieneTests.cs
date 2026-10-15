using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Guards the committed Reasonix transcripts against leaking personal data: home-directory paths, skill
/// descriptions and unredacted session ids.
/// </summary>
public sealed class ReasonixFixtureHygieneTests
{
    private const string PlaceholderCommandDescription = "placeholder";

    [Test]
    public void Committed_reasonix_fixtures_pass_hygiene_checks()
    {
        var violations = ReasonixFixtureHygiene.Scan(ReasonixFixtureHygiene.CommittedFixtureDirectory);

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Committed_reasonix_fixtures_include_every_required_transcript()
    {
        var expected = new[]
        {
            "initialize", "new-session", "text-turn", "tool-turn", "permission-turn", "load-replay",
            "resume-replay", "cancel-mid-turn", "unknown-model", "unknown-session", "set-config-option-yolo",
            "ask-single", "ask-two-questions", "ask-multi", "ask-reject-option", "ask-cancelled-outcome", "ask-two-turns",
            "ask-delayed-answer", "ask-stop-pending", "ask-resume-pending-part1", "ask-resume-pending"
        };

        foreach (var name in expected)
        {
            File.Exists(Path.Combine(ReasonixFixtureHygiene.CommittedFixtureDirectory, name + ".jsonl"))
                .ShouldBeTrue($"missing transcript {name}.jsonl");
        }
    }

    [TestCase("home-directory path", "{\"dir\":\"in\",\"msg\":{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"fixture-session-01\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"/home/alice/project\"}}}}}")]
    [TestCase("non-placeholder command description", "{\"dir\":\"in\",\"msg\":{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"fixture-session-01\",\"update\":{\"sessionUpdate\":\"available_commands_update\",\"availableCommands\":[{\"name\":\"fixture-command-01\",\"description\":\"Use this skill whenever the user asks about spreadsheets.\"}]}}}}")]
    [TestCase("unredacted session id", "{\"dir\":\"in\",\"msg\":{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"3f9c1a2e-7b4d-4c8e-9a1f-2d6e8b0c4a57\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"ok\"}}}}}")]
    public void Hygiene_check_reports_an_injected_violation_in_a_temporary_copy(string rule, string injectedLine)
    {
        using var copy = new TemporaryFixtureCopy();
        File.AppendAllText(Path.Combine(copy.Path, "initialize.jsonl"), injectedLine + Environment.NewLine);

        var violations = ReasonixFixtureHygiene.Scan(copy.Path);

        violations.ShouldNotBeEmpty();
        violations.ShouldContain(violation => violation.StartsWith("initialize.jsonl: " + rule, StringComparison.Ordinal));
    }

    private static class ReasonixFixtureHygiene
    {
        private static readonly Regex HomeDirectoryPattern = new(
            @"(/home/[^/\s""]+|/Users/[^/\s""]+|/root(/|""|\s)|[A-Za-z]:\\+Users\\+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SessionIdPattern = new(
            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string CommittedFixtureDirectory => Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Reasonix", ReasonixTranscriptFixtures.ReasonixVersion);

        /// <summary>
        /// Returns one message per violation, formatted as <c>file: rule: detail</c>.
        /// </summary>
        public static IReadOnlyList<string> Scan(string directory)
        {
            var violations = new List<string>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl").OrderBy(static file => file, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(path);
                var lineNumber = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (HomeDirectoryPattern.IsMatch(line))
                    {
                        violations.Add($"{fileName}: home-directory path on line {lineNumber}");
                    }

                    if (SessionIdPattern.IsMatch(line))
                    {
                        violations.Add($"{fileName}: unredacted session id on line {lineNumber}");
                    }

                    violations.AddRange(FindCommandDescriptionViolations(fileName, lineNumber, line));
                }
            }

            return violations;
        }

        private static IEnumerable<string> FindCommandDescriptionViolations(string fileName, int lineNumber, string line)
        {
            if (!line.Contains("available_commands_update", StringComparison.Ordinal))
            {
                yield break;
            }

            var root = JsonNode.Parse(line)?["msg"]?["params"]?["update"];
            if (root?["availableCommands"] is not JsonArray commands)
            {
                yield break;
            }

            foreach (var command in commands)
            {
                var description = command?["description"]?.GetValue<string>();
                if (description is not null && description != PlaceholderCommandDescription)
                {
                    yield return $"{fileName}: non-placeholder command description on line {lineNumber}";
                }
            }
        }
    }

    private sealed class TemporaryFixtureCopy : IDisposable
    {
        public TemporaryFixtureCopy()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"hagicode-libs-reasonix-hygiene-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            foreach (var file in Directory.EnumerateFiles(ReasonixFixtureHygiene.CommittedFixtureDirectory, "*.jsonl"))
            {
                File.Copy(file, System.IO.Path.Combine(Path, System.IO.Path.GetFileName(file)));
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
