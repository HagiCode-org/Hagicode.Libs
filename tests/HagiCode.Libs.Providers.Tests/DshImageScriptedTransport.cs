using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Replays dsh ACP frames for image input tests. The initialize and refusal replies come from real dsh 0.2.0-rc.2
/// frames stored under Fixtures/Dsh, so the tests exercise the real wire shape. Only the request id is rewritten.
/// </summary>
internal sealed class DshImageScriptedTransport(
    bool advertiseImage,
    bool rejectInlinePrompts = false,
    string? promptErrorMessage = null) : IAcpTransport
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();

    /// <summary>Every frame the client sent, in order.</summary>
    public List<JsonObject> SentFrames { get; } = [];

    /// <summary>The <c>session/prompt</c> requests, in order.</summary>
    public IReadOnlyList<JsonObject> PromptRequests => SentFrames
        .Where(static frame => frame["method"]?.GetValue<string>() == "session/prompt")
        .ToArray();

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var frame = JsonNode.Parse(message)!.AsObject();
        SentFrames.Add(frame);

        if (frame["id"] is not { } id)
        {
            return Task.CompletedTask;
        }

        switch (frame["method"]?.GetValue<string>())
        {
            case "initialize":
                Reply(LoadFixture(advertiseImage ? "initialize-image-true" : "initialize-image-false")["result"], id);
                break;
            case "session/new":
                Reply(JsonNode.Parse("""{"sessionId":"session-1"}"""), id);
                break;
            case "session/prompt":
                HandlePrompt(frame, id);
                break;
            default:
                Reply(JsonNode.Parse("{}"), id);
                break;
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_inbound.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void HandlePrompt(JsonObject frame, JsonNode id)
    {
        if (promptErrorMessage is not null)
        {
            ReplyError(id, -32000, promptErrorMessage);
            return;
        }

        var hasImageBlock = frame["params"]?["prompt"]?.AsArray()
            .Any(static block => block?["type"]?.GetValue<string>() == "image") == true;
        if (hasImageBlock && (!advertiseImage || rejectInlinePrompts))
        {
            // The real dsh refusal frame, with the request id rewritten.
            var refusal = LoadFixture("session-prompt-rejected-32602");
            refusal["id"] = id.DeepClone();
            Enqueue(refusal);
            return;
        }

        Enqueue(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = JsonNode.Parse("""{"sessionId":"session-1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"ok"}}}""")
        });
        Reply(JsonNode.Parse("""{"stopReason":"end_turn"}"""), id);
    }

    private void Reply(JsonNode? result, JsonNode id)
    {
        Enqueue(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result?.DeepClone()
        });
    }

    private void ReplyError(JsonNode id, int code, string message)
    {
        Enqueue(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });
    }

    private void Enqueue(JsonObject frame)
    {
        _inbound.Writer.TryWrite(frame.ToJsonString());
    }

    private static JsonObject LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dsh", $"{name}.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
