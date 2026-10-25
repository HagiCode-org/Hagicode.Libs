using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Copilot requests rewrite process-wide environment variables (including PATH), so classes that
// spawn child processes must not run alongside them.
[NonParallelizable]
[Category("Integration")]
public sealed class OpenCodeProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Test]
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

    [Test]
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
        var resumedText = resumedMessages[1].Content.GetProperty("text").GetString();
        resumedText.ShouldNotBeNull();
        resumedText.ShouldContain("BLUEPRINT-");
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
    public async Task ExecuteAsync_surfaces_prompt_failure_without_hidden_runtime_replay()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, "unused"));
        server.RemainingPromptFailures = 1;
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var exception = await Should.ThrowAsync<OpenCodeApiException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new OpenCodeOptions { BaseUrl = server.BaseUri.ToString() },
                               "hello"))
            {
            }
        });

        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        server.PromptCount.ShouldBe(1);
        server.CreateSessionCount.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_surfaces_session_creation_failure_without_hidden_runtime_replay()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, _) => OpenCodeTestServer.CreateTextEnvelope(sessionId, "unused"));
        server.RemainingCreateSessionFailures = 1;
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var exception = await Should.ThrowAsync<OpenCodeApiException>(async () =>
        {
            await foreach (var _ in provider.ExecuteAsync(
                               new OpenCodeOptions { BaseUrl = server.BaseUri.ToString() },
                               "hello"))
            {
            }
        });

        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        server.CreateSessionCount.ShouldBe(1);
        server.PromptCount.ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_resumed_session_on_owned_runtime_passes_runtime_resolver_variables_to_the_process()
    {
        await using var server = await OpenCodeTestServer.StartAsync(
            static (_, sessionId, prompt) => OpenCodeTestServer.CreateTextEnvelope(sessionId, prompt));
        await using var attachProvider = CreateProvider(new MissingExecutableResolver());
        var initialMessages = await DrainMessagesAsync(attachProvider.ExecuteAsync(
            new OpenCodeOptions { BaseUrl = server.BaseUri.ToString() },
            "Remember this session."));
        var sessionId = initialMessages[0].Content.GetProperty("session_id").GetString();
        sessionId.ShouldNotBeNullOrWhiteSpace();

        // A fresh provider has no pooled runtime, so resuming the session starts an owned runtime through the resolver.
        var markerPath = Path.Combine(Path.GetTempPath(), $"opencode-provider-marker-{Guid.NewGuid():N}.txt");
        var executablePath = CreateMarkerServeShim(server.BaseUri, markerPath);
        await using var provider = CreateProvider(
            runtimeEnvironmentResolver: new FixedRuntimeEnvironmentResolver(new Dictionary<string, string?>
            {
                ["HAGI_TEST_CONTEXT_MARKER"] = "marker-opencode"
            }));

        try
        {
            var messages = await DrainMessagesAsync(provider.ExecuteAsync(
                new OpenCodeOptions
                {
                    ExecutablePath = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    SessionId = sessionId,
                },
                "What did I ask earlier?"));

            messages[0].Type.ShouldBe("session.resumed");
            messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
            File.ReadAllText(markerPath).ShouldBe("marker-opencode");
        }
        finally
        {
            File.Delete(executablePath);
            File.Delete(markerPath);
        }
    }

    [Test]
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

    [Test]
    public async Task PingAsync_returns_actionable_failure_when_executable_is_missing()
    {
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task PingAsync_can_probe_the_real_opencode_cli_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

        await using var provider = CreateProvider();
        var result = await provider.PingAsync();
        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_attach_forwards_the_configured_server_password_to_a_protected_runtime()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = "provider-forward-secret",
        });
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var messages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions
            {
                BaseUrl = fixture.BaseUri.ToString(),
                ServerPassword = "provider-forward-secret",
            },
            "Reply with the single word READY."));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("READY");
    }

    [Test]
    public async Task ExecuteAsync_attach_without_the_server_password_reports_authentication_failure_without_leaking_it()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = "provider-forward-secret",
        });
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var exception = await Should.ThrowAsync<OpenCodeStandaloneServerLifecycleException>(async () =>
            await DrainMessagesAsync(provider.ExecuteAsync(
                new OpenCodeOptions { BaseUrl = fixture.BaseUri.ToString() },
                "hello")));

        exception.Result.Stage.ShouldBe(OpenCodeLifecycleStage.HealthProbe);
        exception.Message.ShouldContain("authentication failed");
        exception.Message.ShouldNotContain("provider-forward-secret");
    }

    [Test]
    public async Task ExecuteAsync_with_a_handler_yields_the_question_before_the_assistant_and_terminal_messages()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            QuestionModelIds = ["fixture/question-model"],
        });
        await using var provider = CreateProvider(new MissingExecutableResolver());
        var handled = new List<OpenCodeUserInputRequest>();

        var messages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions
            {
                BaseUrl = fixture.BaseUri.ToString(),
                ServerPassword = fixture.ServerPassword,
                Model = "fixture/question-model",
                UserInputHandler = (request, _) =>
                {
                    handled.Add(request);
                    return Task.FromResult<OpenCodeUserInputResponse?>(new OpenCodeUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["Which color do you prefer?"] = ["Blue"],
                        ["Which pets do you like?"] = ["Dogs"],
                    }));
                },
            },
            "Ask me about my preferences."));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "question", "assistant", "terminal.completed"]);
        handled.ShouldHaveSingleItem();
        OpenCodeUserInputRequest.TryFromCliMessage(messages[1], out var question).ShouldBeTrue();
        question.RequestId.ShouldBe(handled[0].RequestId);
        question.Questions.Select(static item => item.Question).ShouldBe(["Which color do you prefer?", "Which pets do you like?"]);
        question.Questions[1].MultiSelect.ShouldBeTrue();
        messages[2].Content.GetProperty("text").GetString().ShouldContain("replied:{\"q0\":\"Blue\",\"q1\":[\"Dogs\"]}");
        fixture.GetForms().Single().State.ShouldBe(OpenCodeFormStates.Replied);
    }

    [Test]
    public async Task ExecuteAsync_without_a_handler_declines_the_question_and_still_completes()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            QuestionModelIds = ["fixture/question-model"],
        });
        await using var provider = CreateProvider(new MissingExecutableResolver());

        var messages = await DrainMessagesAsync(provider.ExecuteAsync(
            new OpenCodeOptions
            {
                BaseUrl = fixture.BaseUri.ToString(),
                ServerPassword = fixture.ServerPassword,
                Model = "fixture/question-model",
            },
            "Ask me about my preferences."));

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldContain("cancelled:" + OpenCodeUserInput.NoHandlerMessage);
        fixture.GetForms().Single().Message.ShouldBe(OpenCodeUserInput.NoHandlerMessage);
    }

    private static TestOpenCodeProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        return new TestOpenCodeProvider(executableResolver ?? new CliExecutableResolver(), runtimeEnvironmentResolver);
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
        var content = $"#!/usr/bin/env bash\nset -euo pipefail\nprintf 'server listening on {baseUri.ToString().TrimEnd('/')}\\n'\nprintf 'server password provider-test-secret\\n'\nwhile true; do sleep 1; done\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>
    /// Writes a serve shim that first records the runtime-resolver marker variable to <paramref name="markerPath" />,
    /// then prints the startup line and keeps running.
    /// </summary>
    private static string CreateMarkerServeShim(Uri baseUri, string markerPath)
    {
        var fileName = $"opencode-marker-shim-{Guid.NewGuid():N}.sh";
        var path = Path.Combine(Path.GetTempPath(), fileName);
        var content = $"#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s' \"$HAGI_TEST_CONTEXT_MARKER\" > '{markerPath}'\nprintf 'server listening on {baseUri.ToString().TrimEnd('/')}\\n'\nwhile true; do sleep 1; done\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class FixedRuntimeEnvironmentResolver(IReadOnlyDictionary<string, string?> environment) : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(environment);
        }
    }

    private sealed class TestOpenCodeProvider : OpenCodeProvider
    {
        public TestOpenCodeProvider(CliExecutableResolver executableResolver, IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
            : base(executableResolver, runtimeEnvironmentResolver)
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

    /// <summary>
    /// Simulates the OpenCode 2.0 HTTP surface. Each prompt's envelope from the responder is published as the 2.0 event sequence.
    /// </summary>
    private sealed class OpenCodeTestServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loopTask;
        private readonly CancellationTokenSource _cts;
        private readonly Func<OpenCodeTestServer, string, string, OpenCodeMessageEnvelope> _promptResponder;
        private readonly ConcurrentDictionary<string, OpenCodeSession> _sessions = new(StringComparer.Ordinal);
        private readonly List<Channel<string>> _subscribers = [];
        private readonly object _subscriberLock = new();
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

        public int PromptCount { get; private set; }

        public int RemainingCreateSessionFailures { get; set; }

        public int RemainingPromptFailures { get; set; }

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
                if (request.HttpMethod == "GET" && path == "/api/info")
                {
                    await WriteJsonAsync(context.Response, new { version = Version, pid = 1, urls = Array.Empty<string>(), paths = new { tmp = "/tmp" } }, cancellationToken);
                    return;
                }

                if (request.HttpMethod == "GET" && path == "/api/session")
                {
                    await WriteJsonAsync(context.Response, new { data = _sessions.Values.ToArray(), cursor = new { previous = (string?)null, next = (string?)null } }, cancellationToken);
                    return;
                }

                if (request.HttpMethod == "POST" && path == "/api/session")
                {
                    CreateSessionCount++;
                    if (RemainingCreateSessionFailures > 0)
                    {
                        RemainingCreateSessionFailures--;
                        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        await WriteStringAsync(context.Response, "session unavailable", cancellationToken);
                        return;
                    }

                    using var body = await JsonDocument.ParseAsync(request.InputStream, cancellationToken: cancellationToken);
                    var directory = body.RootElement.TryGetProperty("location", out var location) && location.TryGetProperty("directory", out var dir)
                        ? dir.GetString()
                        : null;
                    var number = Interlocked.Increment(ref _nextSessionId);
                    var session = new OpenCodeSession
                    {
                        Id = $"session-{number:D4}",
                        Title = $"Test Session {number}",
                        Location = directory is null ? null : new OpenCodeLocationRef { Directory = directory },
                        ProjectId = "test-project"
                    };
                    _sessions[session.Id] = session;
                    await WriteJsonAsync(context.Response, new { data = session }, cancellationToken);
                    return;
                }

                if (request.HttpMethod == "GET" && path == "/api/event")
                {
                    await StreamEventsAsync(context.Response, cancellationToken);
                    return;
                }

                if (request.HttpMethod == "POST" && TryMatchSessionAction(path, "model", out _))
                {
                    context.Response.StatusCode = 204;
                    return;
                }

                if (request.HttpMethod == "POST" && TryMatchSessionAction(path, "prompt", out var sessionIdForPrompt))
                {
                    if (!_sessions.ContainsKey(sessionIdForPrompt))
                    {
                        context.Response.StatusCode = 404;
                        await WriteStringAsync(context.Response, "Not Found", cancellationToken);
                        return;
                    }

                    using var promptBody = await JsonDocument.ParseAsync(request.InputStream, cancellationToken: cancellationToken);
                    var promptText = promptBody.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
                    PromptCount++;
                    if (RemainingPromptFailures > 0)
                    {
                        RemainingPromptFailures--;
                        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        await WriteStringAsync(context.Response, "prompt unavailable", cancellationToken);
                        return;
                    }

                    var envelope = _promptResponder(this, sessionIdForPrompt, promptText);
                    PublishTurn(sessionIdForPrompt, envelope);
                    await WriteJsonAsync(context.Response, new
                    {
                        data = new
                        {
                            id = $"msg_{Guid.NewGuid():N}",
                            sessionID = sessionIdForPrompt,
                            type = "user",
                            payload = new { text = promptText },
                            delivery = "steer",
                        }
                    }, cancellationToken);
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

        /// <summary>
        /// Publishes the 2.0 event sequence for one turn: text parts become <c>session.text.ended</c>; an error part fails the turn.
        /// </summary>
        private void PublishTurn(string sessionId, OpenCodeMessageEnvelope envelope)
        {
            var assistantMessageId = envelope.MessageId ?? $"msg_{Guid.NewGuid():N}";
            PublishEvent("session.execution.started", new { sessionID = sessionId });

            var errorPart = envelope.Parts.FirstOrDefault(static part => part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("type", out var type) && type.GetString() == "error");
            if (errorPart.ValueKind == JsonValueKind.Object)
            {
                var message = errorPart.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;
                PublishEvent("session.execution.failed", new
                {
                    sessionID = sessionId,
                    error = new { type = "test.failure", message = message ?? "OpenCode test turn failed." },
                });
                return;
            }

            var ordinal = 0;
            foreach (var part in envelope.Parts)
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                    part.TryGetProperty("text", out var textNode))
                {
                    PublishEvent("session.text.ended", new
                    {
                        sessionID = sessionId,
                        assistantMessageID = assistantMessageId,
                        ordinal = ordinal++,
                        text = textNode.GetString(),
                    });
                }
            }

            PublishEvent("session.execution.succeeded", new { sessionID = sessionId });
        }

        private void PublishEvent(string type, object data)
        {
            var json = JsonSerializer.Serialize(new
            {
                id = $"evt_{Guid.NewGuid():N}",
                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                type,
                data,
            });

            lock (_subscriberLock)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(json);
                }
            }
        }

        private async Task StreamEventsAsync(HttpListenerResponse response, CancellationToken cancellationToken)
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            var channel = Channel.CreateUnbounded<string>();
            lock (_subscriberLock)
            {
                _subscribers.Add(channel);
            }

            try
            {
                await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type = "server.connected", data = new { } })}\n\n");
                await writer.FlushAsync(cancellationToken);
                await foreach (var json in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    await writer.WriteAsync($"data: {json}\n\n");
                    await writer.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (HttpListenerException)
            {
            }
            finally
            {
                lock (_subscriberLock)
                {
                    _subscribers.Remove(channel);
                }

                channel.Writer.TryComplete();
            }
        }

        private static bool TryMatchSessionAction(string path, string action, out string sessionId)
        {
            sessionId = string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 4 &&
                string.Equals(segments[0], "api", StringComparison.Ordinal) &&
                string.Equals(segments[1], "session", StringComparison.Ordinal) &&
                string.Equals(segments[3], action, StringComparison.Ordinal))
            {
                sessionId = Uri.UnescapeDataString(segments[2]);
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
