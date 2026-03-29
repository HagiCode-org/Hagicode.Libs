using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class OpenCodeProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Fact]
    public async Task ExecuteAsync_uses_attach_runtime_and_normalizes_messages()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, "pong"));
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var messages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions { BaseUrl = server.BaseUri.ToString() },
            "Reply with exactly the word 'pong'"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("started");
        messages[0].Content.GetProperty("runtimeFingerprint").GetString().ShouldNotBeNullOrWhiteSpace();
        messages[0].Content.GetProperty("poolFingerprint").GetString()
            .ShouldBe(messages[0].Content.GetProperty("sessionId").GetString());
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Fact]
    public async Task ExecuteAsync_resumes_existing_attach_session()
    {
        var rememberedSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var server = await OpenCodeTestServer.StartAsync(
            (_, sessionId, prompt) =>
            {
                const string marker = "Remember the secret word:";
                if (prompt.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    var start = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    var secret = prompt[(start + marker.Length)..]
                        .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault() ?? string.Empty;
                    rememberedSecrets[sessionId] = secret;
                    return OpenCodeTestServer.CreateTextEnvelope(sessionId, "ACK");
                }

                if (prompt.Contains("What was the secret word", StringComparison.OrdinalIgnoreCase)
                    && rememberedSecrets.TryGetValue(sessionId, out var remembered))
                {
                    return OpenCodeTestServer.CreateTextEnvelope(sessionId, remembered);
                }

                return OpenCodeTestServer.CreateTextEnvelope(sessionId, prompt);
            });
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var initialMessages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions { BaseUrl = server.BaseUri.ToString() },
            $"Remember the secret word: BLUEPRINT-{Guid.NewGuid():N}. Reply with exactly ACK."));
        var sessionId = initialMessages[0].Content.GetProperty("session_id").GetString();

        var resumedMessages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions
            {
                BaseUrl = server.BaseUri.ToString(),
                SessionId = sessionId,
            },
            "What was the secret word I told you earlier? Reply with just the word."));

        resumedMessages[0].Type.ShouldBe("session.resumed");
        resumedMessages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
        resumedMessages[0].Content.GetProperty("requestedSessionId").GetString().ShouldBe(sessionId);
        resumedMessages[0].Content.GetProperty("poolFingerprint").GetString().ShouldBe(sessionId);
        resumedMessages[1].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
        resumedMessages[1].Content.GetProperty("text").GetString().ShouldContain("BLUEPRINT-");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_new_session_when_requested_session_cannot_be_resumed()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, "pong"));
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var messages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions
            {
                BaseUrl = server.BaseUri.ToString(),
                SessionId = "missing-session"
            },
            "Reply with exactly the word 'pong'"));

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("missing-session");
        messages[0].Content.GetProperty("requestedSessionId").GetString().ShouldBe("missing-session");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("restarted");
        messages[0].Content.GetProperty("poolFingerprint").GetString().ShouldBe("missing-session");
        messages[0].Content.GetProperty("restarted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_uses_attach_runtime_when_base_url_is_supplied()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, "attach-pong"));
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var messages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions
            {
                BaseUrl = server.BaseUri.ToString(),
                WorkingDirectory = "/tmp/opencode-attach"
            },
            "hello"));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("attach-pong");
        server.CreateSessionCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_uses_live_runtime_when_no_base_url_is_supplied()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, "live-pong"));
        var executablePath = CreateServeShim(server.BaseUri);
        await using var provider = CreateProvider();

        try
        {
            var messages = await DrainMessagesAsync(provider.ExecuteAsync(
                new OpenCodeOptions
                {
                    ExecutablePath = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)
                },
                "hello"));

            messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
            messages[1].Content.GetProperty("text").GetString().ShouldBe("live-pong");
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_preserves_multiline_assistant_content()
    {
        const string multiline = "Paragraph one.\n\n- item one\n- item two\n\n```txt\nblock\n```";
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, multiline));
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var messages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions { BaseUrl = server.BaseUri.ToString() },
            "hello"));

        messages[1].Content.GetProperty("text").GetString().ShouldBe(multiline);
        messages[1].Content.GetProperty("sessionId").GetString().ShouldNotBeNullOrWhiteSpace();
        messages[1].Content.GetProperty("resumeMode").GetString().ShouldBe("started");
        messages[2].Content.GetProperty("text").GetString().ShouldBe(multiline);
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_diagnostic_when_response_has_no_text_content()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateDiagnosticEnvelope(sessionId, "response missing text"));
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new OpenCodeOptions { BaseUrl = server.BaseUri.ToString() },
                               "hello"))
            {
            }
        });

        exception.Message.ShouldContain("response missing text");
    }

    [Fact]
    public async Task PingAsync_reports_success_and_version_when_runtime_is_healthy()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, "pong"),
            version: "opencode-test-1.0.0");
        var executablePath = CreateServeShim(server.BaseUri);
        await using var provider = CreateProvider();

        try
        {
            var result = await provider.ExecutePingWithExecutableAsync(executablePath);

            result.Success.ShouldBeTrue();
            result.ProviderName.ShouldBe("opencode");
            result.Version.ShouldBe("opencode-test-1.0.0");
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Fact]
    public async Task PingAsync_returns_actionable_failure_when_executable_is_missing()
    {
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task PingAsync_can_probe_the_real_opencode_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        await using var provider = CreateProvider();
        var result = await provider.PingAsync();
        result.Success.ShouldBeTrue();
    }

    private static TestOpenCodeProvider CreateProvider(CliExecutableResolver? executableResolver = null)
    {
        return new TestOpenCodeProvider(executableResolver ?? new CliExecutableResolver());
    }

    private static async Task<List<CliMessage>> DrainMessagesAsync(IAsyncEnumerable<CliMessage> stream)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        return messages;
    }

    private static string CreateServeShim(Uri baseUri)
    {
        var fileName = $"opencode-shim-{Guid.NewGuid():N}.sh";
        var path = Path.Combine(Path.GetTempPath(), fileName);
        var content = $"#!/usr/bin/env bash\nset -euo pipefail\nprintf 'opencode server listening on {baseUri}\\n'\nwhile true; do sleep 1; done\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestOpenCodeProvider : OpenCodeProvider
    {
        public TestOpenCodeProvider(CliExecutableResolver executableResolver)
            : base(executableResolver, runtimeEnvironmentResolver: null)
        {
        }

        public Task<CliProviderTestResult> ExecutePingWithExecutableAsync(string executablePath)
        {
            return PingWithOptionsAsync(new OpenCodeOptions { ExecutablePath = executablePath });
        }

        private async Task<CliProviderTestResult> PingWithOptionsAsync(OpenCodeOptions options)
        {
            await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
            var health = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
            {
                ExecutablePath = options.ExecutablePath,
                BaseUrl = options.BaseUrl,
                WorkingDirectory = options.WorkingDirectory,
                Workspace = options.Workspace,
                StartupTimeout = options.StartupTimeout,
                RequestTimeout = options.RequestTimeout,
                EnvironmentVariables = options.EnvironmentVariables,
                ExtraArguments = options.ExtraArguments,
            });
            return new CliProviderTestResult
            {
                ProviderName = Name,
                Success = health.Status == OpenCodeStandaloneServerStatus.Ready,
                Version = health.Version,
                ErrorMessage = health.ErrorMessage,
            };
        }
    }

    private sealed class MissingExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;
    }

    private sealed class OpenCodeTestServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loopTask;
        private readonly CancellationTokenSource _cts;
        private readonly Func<OpenCodeTestServer, string, string, OpenCodeMessageEnvelope> _promptResponder;
        private readonly ConcurrentDictionary<string, OpenCodeSession> _sessions = new(StringComparer.Ordinal);
        private int _nextSessionId;
        private int _disposed;

        private OpenCodeTestServer(
            HttpListener listener,
            Uri baseUri,
            Task loopTask,
            CancellationTokenSource cts,
            string version,
            Func<OpenCodeTestServer, string, string, OpenCodeMessageEnvelope> promptResponder)
        {
            _listener = listener;
            BaseUri = baseUri;
            _loopTask = loopTask;
            _cts = cts;
            Version = version;
            _promptResponder = promptResponder;
        }

        public Uri BaseUri { get; }

        public string Version { get; }

        public int CreateSessionCount { get; private set; }

        public static async Task<OpenCodeTestServer> StartAsync(
            Func<OpenCodeTestServer, string, string, OpenCodeMessageEnvelope> promptResponder,
            string version = "opencode-test-1.0.0")
        {
            var port = OpenCodeProcessLauncher.GetFreeTcpPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var cts = new CancellationTokenSource();
            OpenCodeTestServer? server = null;
            var loopTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    HttpListenerContext? context = null;
                    try
                    {
                        context = await listener.GetContextAsync().WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (HttpListenerException) when (cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (context is not null)
                    {
                        _ = Task.Run(() => server!.HandleAsync(context, cts.Token), CancellationToken.None);
                    }
                }
            }, CancellationToken.None);

            server = new OpenCodeTestServer(listener, new Uri($"http://127.0.0.1:{port}/"), loopTask, cts, version, promptResponder);
            await Task.Yield();
            return server;
        }

        public static OpenCodeMessageEnvelope CreateTextEnvelope(string sessionId, string text)
        {
            return new OpenCodeMessageEnvelope
            {
                Info = JsonSerializer.SerializeToElement(new
                {
                    id = $"message-{Guid.NewGuid():N}",
                    sessionID = sessionId,
                    role = "assistant"
                }),
                Parts =
                [
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "text",
                        text
                    })
                ]
            };
        }

        public static OpenCodeMessageEnvelope CreateDiagnosticEnvelope(string sessionId, string message)
        {
            return new OpenCodeMessageEnvelope
            {
                Info = JsonSerializer.SerializeToElement(new
                {
                    id = $"message-{Guid.NewGuid():N}",
                    sessionID = sessionId,
                    role = "assistant"
                }),
                Parts =
                [
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "error",
                        message
                    })
                ]
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try
            {
                await _loopTask;
            }
            catch
            {
            }
            finally
            {
                _cts.Dispose();
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var request = context.Request;
                var path = request.Url?.AbsolutePath ?? "/";
                if (request.HttpMethod == "GET" && path == "/global/health")
                {
                    await WriteJsonAsync(context.Response, new { healthy = true, version = Version }, cancellationToken);
                    return;
                }

                if (request.HttpMethod == "GET" && path == "/session")
                {
                    await WriteJsonAsync(context.Response, _sessions.Values.ToArray(), cancellationToken);
                    return;
                }

                if (request.HttpMethod == "POST" && path == "/session")
                {
                    CreateSessionCount++;
                    var sessionId = $"session-{Interlocked.Increment(ref _nextSessionId):D4}";
                    var session = new OpenCodeSession
                    {
                        Id = sessionId,
                        Title = $"Test Session {_nextSessionId}",
                        Directory = request.QueryString["directory"],
                        WorkspaceId = request.QueryString["workspace"],
                        ProjectId = "test-project"
                    };
                    _sessions[sessionId] = session;
                    await WriteJsonAsync(context.Response, session, cancellationToken);
                    return;
                }

                if (TryMatchPromptPath(path, out var sessionIdForPrompt))
                {
                    if (!_sessions.ContainsKey(sessionIdForPrompt))
                    {
                        context.Response.StatusCode = 404;
                        await WriteStringAsync(context.Response, "Not Found", cancellationToken);
                        return;
                    }

                    var promptRequest = await JsonSerializer.DeserializeAsync<OpenCodeSessionPromptRequest>(request.InputStream, cancellationToken: cancellationToken)
                        ?? new OpenCodeSessionPromptRequest();
                    var promptText = promptRequest.Parts.FirstOrDefault(static part => string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))?.Text
                        ?? string.Empty;
                    var response = _promptResponder(this, sessionIdForPrompt, promptText);
                    await WriteJsonAsync(context.Response, response, cancellationToken);
                    return;
                }

                context.Response.StatusCode = 404;
                await WriteStringAsync(context.Response, "Not Found", cancellationToken);
            }
            finally
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    context.Response.Close();
                }
            }
        }

        private static bool TryMatchPromptPath(string path, out string sessionId)
        {
            sessionId = string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 3 &&
                string.Equals(segments[0], "session", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[2], "message", StringComparison.OrdinalIgnoreCase))
            {
                sessionId = Uri.UnescapeDataString(segments[1]);
                return true;
            }

            return false;
        }

        private static async Task WriteJsonAsync<T>(HttpListenerResponse response, T payload, CancellationToken cancellationToken)
        {
            response.StatusCode = 200;
            response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(response.OutputStream, payload, cancellationToken: cancellationToken);
        }

        private static async Task WriteStringAsync(HttpListenerResponse response, string value, CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(value.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
    }
}
