using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// An in-process stand-in for <c>codex app-server</c>. The client reads and writes it as text streams, one JSON message per
/// line, so the tests drive the real client over the same protocol the process speaks.
/// </summary>
internal sealed class CodexAppServerScriptedServer
{
    private readonly Channel<string> _toClient = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();
    private readonly List<JsonObject> _unclaimed = [];

    public CodexAppServerScriptedServer()
    {
        ClientReader = new ChannelTextReader(_toClient.Reader);
        ClientWriter = new ChannelTextWriter(_fromClient.Writer);
    }

    /// <summary>The reader the client gets as the app-server's standard output.</summary>
    public TextReader ClientReader { get; }

    /// <summary>The writer the client gets as the app-server's standard input.</summary>
    public TextWriter ClientWriter { get; }

    /// <summary>Every message the client has written so far, in order.</summary>
    public List<JsonObject> Received { get; } = [];

    /// <summary>Sends one message to the client.</summary>
    public void Send(JsonObject message) => _toClient.Writer.TryWrite(message.ToJsonString());

    /// <summary>Sends a notification (a message with a method and no id).</summary>
    public void Notify(string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        Send(message);
    }

    /// <summary>Sends a server request, such as <c>item/tool/requestUserInput</c>.</summary>
    public void Request(JsonNode id, string method, JsonObject parameters)
    {
        Send(new JsonObject
        {
            ["id"] = id.DeepClone(),
            ["method"] = method,
            ["params"] = parameters
        });
    }

    /// <summary>Ends the output stream, as a process that exited would.</summary>
    public void Close() => _toClient.Writer.TryComplete();

    /// <summary>
    /// Waits for the next client request with the given method and answers it with <paramref name="result"/>.
    /// Other messages the client writes meanwhile are kept for <see cref="ExpectMessageAsync"/>.
    /// </summary>
    public async Task<JsonObject> AnswerAsync(string method, JsonObject result, CancellationToken cancellationToken)
    {
        var request = await ExpectRequestAsync(method, cancellationToken).ConfigureAwait(false);
        Send(new JsonObject
        {
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = result
        });
        return request;
    }

    /// <summary>
    /// Waits for the next message from the client that has the given method and no id, such as <c>initialized</c>.
    /// </summary>
    public Task<JsonObject> ExpectNotificationAsync(string method, CancellationToken cancellationToken)
    {
        return ExpectMessageAsync(message => message["method"]?.GetValue<string>() == method && message["id"] is null, cancellationToken);
    }

    /// <summary>Waits for the next client request with the given method.</summary>
    public Task<JsonObject> ExpectRequestAsync(string method, CancellationToken cancellationToken)
    {
        return ExpectMessageAsync(message => message["method"]?.GetValue<string>() == method && message["id"] is not null, cancellationToken);
    }

    /// <summary>
    /// Waits for a client message that matches. A message that matched an earlier wait is not matched again.
    /// </summary>
    public async Task<JsonObject> ExpectMessageAsync(Func<JsonObject, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_unclaimed)
            {
                var index = _unclaimed.FindIndex(message => predicate(message));
                if (index >= 0)
                {
                    var found = _unclaimed[index];
                    _unclaimed.RemoveAt(index);
                    return found;
                }
            }

            var line = await _fromClient.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var message = JsonNode.Parse(line)!.AsObject();
            Received.Add(message);
            lock (_unclaimed)
            {
                _unclaimed.Add(message);
            }
        }
    }

    /// <summary>Returns the reply the client sent for a server request id, once it arrives.</summary>
    public Task<JsonObject> ExpectReplyAsync(JsonNode id, CancellationToken cancellationToken)
    {
        return ExpectMessageAsync(
            message => message["method"] is null && message["id"] is not null && message["id"]!.ToJsonString() == id.ToJsonString(),
            cancellationToken);
    }

    /// <summary>Whether a reply for the request id has already been written by the client.</summary>
    public bool HasReply(JsonNode id)
    {
        lock (_unclaimed)
        {
            var key = id.ToJsonString();
            return Received.Any(message => message["method"] is null && message["id"] is not null && message["id"]!.ToJsonString() == key);
        }
    }

    private sealed class ChannelTextReader(ChannelReader<string> reader) : TextReader
    {
        public override string? ReadLine() => ReadLineAsync().GetAwaiter().GetResult();

        public override async Task<string?> ReadLineAsync()
        {
            try
            {
                return await reader.ReadAsync().ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    private sealed class ChannelTextWriter(ChannelWriter<string> writer) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            // The client writes whole lines through WriteLineAsync; a bare Write is kept for completeness.
            if (value is not null)
            {
                writer.TryWrite(value.TrimEnd('\n'));
            }
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            writer.TryWrite(buffer.ToString());
            return Task.CompletedTask;
        }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Task.CompletedTask;
    }
}
