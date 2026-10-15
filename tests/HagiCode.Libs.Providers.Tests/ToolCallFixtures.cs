using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Tools;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.DeepAgents;
using HagiCode.Libs.Providers.Dsh;
using HagiCode.Libs.Providers.Gemini;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Junie;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using HagiCode.Libs.Providers.Omp;
using HagiCode.Libs.Providers.Pi;
using HagiCode.Libs.Providers.QoderCli;
using HagiCode.Libs.Providers.Reasonix;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// The tool-call fixture format, applied to every file under <c>Fixtures/ToolCalls/&lt;provider&gt;/</c>.
/// </summary>
/// <remarks>
/// Each <c>.jsonl</c> file starts with one header object on its first line:
/// <c>{"fixtureHeader":{"provenance":"recorded|synthetic","source":"...","format":"...","notes":"optional"},"expectedToolKinds":{"&lt;toolCallId&gt;":"&lt;kind&gt;"}}</c>.
/// <c>provenance</c> is <c>recorded</c> when the stream came from a real CLI run (the source names the file or run),
/// otherwise <c>synthetic</c>, and <c>source</c> is required in both cases. <c>format</c> selects how the remaining lines
/// are replayed: <c>acp-jsonrpc</c> (one <c>session/update</c> notification per line), <c>pi-json-events</c>,
/// <c>omp-json-events</c> (one event per line for the streaming mapper), <c>claude-stream-json</c> (one assistant message per
/// line) or <c>codex-items</c> (one thread item per line). Each expected id must appear in at least one replayed tool message
/// with that kind, and every replayed tool message must carry a vocabulary kind.
/// </remarks>
internal sealed record ToolCallFixtureFile(
    string Path,
    string Provenance,
    string Source,
    string Format,
    IReadOnlyDictionary<string, string> ExpectedToolKinds,
    IReadOnlyList<string> Lines)
{
    private static readonly string[] Provenances = ["recorded", "synthetic"];

    public static string Root => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "ToolCalls");

    public static string DirectoryOf(string provider) => System.IO.Path.Combine(Root, provider);

    public static bool HasProviderDirectory(string provider) => Directory.Exists(DirectoryOf(provider));

    public static IReadOnlyList<string> ProviderDirectoryNames()
    {
        return Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Select(System.IO.Path.GetFileName).Where(static name => name is not null).Select(static name => name!)
                .OrderBy(static name => name, StringComparer.Ordinal).ToArray()
            : [];
    }

    public static IReadOnlyList<ToolCallFixtureFile> LoadProvider(string provider)
    {
        var directory = DirectoryOf(provider);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, "*.jsonl").OrderBy(static path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToArray();
    }

    public static ToolCallFixtureFile Load(string path)
    {
        var lines = File.ReadAllLines(path).Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
        lines.ShouldNotBeEmpty(path);

        using var header = JsonDocument.Parse(lines[0]);
        var root = header.RootElement;
        root.TryGetProperty("fixtureHeader", out var headerElement).ShouldBeTrue($"{path}: first line must be the fixtureHeader object");

        var provenance = headerElement.GetProperty("provenance").GetString();
        Provenances.ShouldContain(provenance, $"{path}: provenance must be recorded or synthetic");
        var source = headerElement.GetProperty("source").GetString();
        string.IsNullOrWhiteSpace(source).ShouldBeFalse($"{path}: source is required");
        var format = headerElement.GetProperty("format").GetString();
        string.IsNullOrWhiteSpace(format).ShouldBeFalse($"{path}: format is required");

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("expectedToolKinds", out var expectedElement))
        {
            foreach (var property in expectedElement.EnumerateObject())
            {
                expected[property.Name] = property.Value.GetString()!;
            }
        }

        return new ToolCallFixtureFile(path, provenance!, source!, format!, expected, lines.Skip(1).ToArray());
    }
}

/// <summary>
/// The providers whose Libs stream carries tool calls that the classifier resolves: the ACP family that maps
/// <c>tool_call</c> updates, the Pi and OMP event mappers, and the Claude and Codex names that the classifier aliases.
/// Copilot and OpenCode are not listed because their Libs streams carry no kind mapping in this change.
/// </summary>
internal static class ToolCallFixtureCatalog
{
    public static readonly IReadOnlyCollection<string> ToolCallEmittingProviders = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-code", "codex", "codebuddy", "deepagents", "dsh", "gemini", "hermes", "junie", "kimi", "kiro-cli",
        "omp", "pi", "qodercli", "reasonix",
    };
}

/// <summary>
/// One tool message produced by replaying a fixture: its message type, the tool call id, and the canonical kind.
/// </summary>
internal sealed record ToolKindObservation(string MessageType, string? ToolCallId, string? Kind);

/// <summary>
/// Replays fixture lines through the provider mapper that produces their tool messages.
/// </summary>
internal static class ToolCallFixtureReplay
{
    private static readonly string[] ToolMessageTypes = ["tool.call", "tool.update", "tool.completed", "tool.failed"];

    public static IReadOnlyList<ToolKindObservation> Replay(string provider, ToolCallFixtureFile file)
    {
        return file.Format switch
        {
            "acp-jsonrpc" => ReplayAcp(provider, file.Lines),
            "pi-json-events" => ReplayPi(file.Lines),
            "omp-json-events" => ReplayOmp(file.Lines),
            "claude-stream-json" => ReplayClaude(file.Lines),
            "codex-items" => ReplayCodex(file.Lines),
            _ => throw new InvalidOperationException($"{file.Path}: unknown fixture format '{file.Format}'.")
        };
    }

    private static IReadOnlyList<ToolKindObservation> ReplayAcp(string provider, IReadOnlyList<string> lines)
    {
        Func<AcpNotification, IReadOnlyList<CliMessage>> normalize = provider switch
        {
            "gemini" => GeminiAcpMessageMapper.NormalizeNotification,
            "kimi" => KimiAcpMessageMapper.NormalizeNotification,
            "junie" => JunieAcpMessageMapper.NormalizeNotification,
            "kiro-cli" => KiroAcpMessageMapper.NormalizeNotification,
            "qodercli" => QoderCliAcpMessageMapper.NormalizeNotification,
            "reasonix" => notification => ReasonixAcpMessageMapper.NormalizeNotification(notification),
            "codebuddy" => CodebuddyAcpMessageMapper.NormalizeNotification,
            "deepagents" => DeepAgentsAcpMessageMapper.NormalizeNotification,
            "hermes" => HermesAcpMessageMapper.NormalizeNotification,
            "dsh" => DshAcpMessageMapper.NormalizeNotification,
            _ => throw new InvalidOperationException($"No ACP mapper is registered for fixture provider '{provider}'.")
        };

        var observations = new List<ToolKindObservation>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()!;
            var parameters = root.GetProperty("params").Clone();
            var rawKind = ReadString(parameters.GetProperty("update"), "kind");

            foreach (var message in normalize(new AcpNotification(method, parameters)))
            {
                if (!ToolMessageTypes.Contains(message.Type, StringComparer.Ordinal))
                {
                    continue;
                }

                var update = message.Content.GetProperty("update");
                // The raw ACP kind must survive annotation unchanged.
                if (rawKind is not null)
                {
                    ReadString(update, "kind").ShouldBe(rawKind);
                }

                observations.Add(new ToolKindObservation(
                    message.Type,
                    ReadString(message.Content, "tool_call_id") ?? ReadString(update, "toolCallId"),
                    ReadString(update, "toolKind")));
            }
        }

        return observations;
    }

    private static IReadOnlyList<ToolKindObservation> ReplayPi(IReadOnlyList<string> lines)
    {
        var state = new PiJsonEventMapper().CreateStreamingState();
        var messages = new List<CliMessage>();
        foreach (var line in lines)
        {
            messages.AddRange(state.ProcessOutputLine(line));
        }

        messages.AddRange(state.Complete(0, null));
        return ToolObservations(messages);
    }

    private static IReadOnlyList<ToolKindObservation> ReplayOmp(IReadOnlyList<string> lines)
    {
        var state = new OmpJsonEventMapper().CreateStreamingState(null);
        var messages = new List<CliMessage>();
        foreach (var line in lines)
        {
            messages.AddRange(state.ProcessOutputLine(line));
        }

        messages.AddRange(state.Complete(0, string.Empty));
        return ToolObservations(messages);
    }

    private static IReadOnlyList<ToolKindObservation> ToolObservations(IEnumerable<CliMessage> messages)
    {
        var observations = new List<ToolKindObservation>();
        foreach (var message in messages)
        {
            if (!ToolMessageTypes.Contains(message.Type, StringComparer.Ordinal))
            {
                continue;
            }

            var update = message.Content.TryGetProperty("update", out var updateElement) ? updateElement : default;
            observations.Add(new ToolKindObservation(
                message.Type,
                ReadString(message.Content, "tool_call_id") ?? ReadString(update, "toolCallId"),
                ReadString(update, "kind")));
        }

        return observations;
    }

    private static IReadOnlyList<ToolKindObservation> ReplayClaude(IReadOnlyList<string> lines)
    {
        var observations = new List<ToolKindObservation>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (ReadString(block, "type") != "tool_use")
                {
                    continue;
                }

                var inputJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : null;
                var kind = ToolKindClassifier.Resolve(null, ReadString(block, "name"), inputJson).Kind;
                observations.Add(new ToolKindObservation("tool.call", ReadString(block, "id"), kind));
            }
        }

        return observations;
    }

    private static IReadOnlyList<ToolKindObservation> ReplayCodex(IReadOnlyList<string> lines)
    {
        var observations = new List<ToolKindObservation>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            var id = ReadString(item, "id");
            switch (ReadString(item, "type"))
            {
                case "command_execution":
                    observations.Add(new ToolKindObservation("tool.call", id, ToolKindClassifier.Resolve(null, "command_execution", null).Kind));
                    break;
                case "mcp_tool_call":
                    var resolution = ToolKindClassifier.Resolve(null, ReadString(item, "tool"), null, ReadString(item, "server"));
                    observations.Add(new ToolKindObservation("tool.call", id, resolution.Kind));
                    break;
            }
        }

        return observations;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
