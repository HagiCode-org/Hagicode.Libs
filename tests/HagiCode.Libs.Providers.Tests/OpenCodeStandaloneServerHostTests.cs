using System.Net;
using System.Text;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Copilot requests rewrite process-wide environment variables (including PATH), so classes that
// spawn child processes must not run alongside them.
[NonParallelizable]
[Category("Integration")]
public sealed class OpenCodeStandaloneServerHostTests
{
    private const string HostPassword = "host-test-secret";

    [Test]
    public async Task WarmupAsync_with_live_runtime_returns_ready_diagnostics()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = HostPassword,
        });
        var executablePath = CreateServeShim(fixture.BaseUri, fixture.ServerPassword);
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());

        try
        {
            var result = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
            {
                ExecutablePath = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
            });

            result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Ready);
            result.Stage.ShouldBe(OpenCodeLifecycleStage.Ready);
            result.ExecutionProfile.ShouldBe("live");
            result.OwnsRuntime.ShouldBeTrue();
            result.BaseUri.ShouldBe(fixture.BaseUri.ToString());
            result.Version.ShouldBe("2.0.32");
            result.LastSucceededAt.ShouldNotBeNull();

            // The probe succeeded only because the password printed at startup was captured and sent.
            // The printed password must not survive into the diagnostics.
            result.DiagnosticOutput.ShouldContain("server password <redacted>");
            result.DiagnosticOutput.ShouldNotContain(HostPassword);
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Test]
    public async Task WarmupAsync_when_owned_runtime_prints_no_password_still_becomes_ready()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            RequireAuthentication = false,
        });
        var executablePath = CreateServeShim(fixture.BaseUri, password: null);
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());

        try
        {
            var result = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
            {
                ExecutablePath = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
            });

            result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Ready);
            result.DiagnosticOutput.ShouldNotContain("server password");
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Test]
    public async Task AcquireAsync_concurrent_live_requests_collapse_to_one_runtime()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = HostPassword,
        });
        var executablePath = CreateServeShim(fixture.BaseUri, fixture.ServerPassword);
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        var options = new OpenCodeStandaloneServerOptions
        {
            ExecutablePath = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
        };

        try
        {
            var runtimes = await Task.WhenAll(
                host.AcquireAsync(options),
                host.AcquireAsync(options),
                host.AcquireAsync(options));

            runtimes.Select(static runtime => runtime.BaseUri).Distinct().ShouldHaveSingleItem();
            ReferenceEquals(runtimes[0], runtimes[1]).ShouldBeTrue();
            ReferenceEquals(runtimes[1], runtimes[2]).ShouldBeTrue();
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Test]
    public async Task WarmupAsync_attach_with_configured_password_is_ready()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = HostPassword,
        });
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());

        var result = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
        {
            BaseUrl = fixture.BaseUri.ToString(),
            ServerPassword = HostPassword,
        });

        result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Ready);
        result.ExecutionProfile.ShouldBe("attach");
        result.Version.ShouldBe("2.0.32");
    }

    [Test]
    public async Task WarmupAsync_attach_without_password_against_protected_runtime_reports_authentication_failure()
    {
        await using var fixture = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
        {
            ServerPassword = HostPassword,
        });
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());

        var result = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
        {
            BaseUrl = fixture.BaseUri.ToString(),
        });

        result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Unhealthy);
        result.Stage.ShouldBe(OpenCodeLifecycleStage.HealthProbe);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("health_probe");
        result.ErrorMessage.ShouldContain("authentication failed");
        result.ErrorMessage.ShouldNotContain(HostPassword);
    }

    [Test]
    public async Task WarmupAsync_attach_to_legacy_html_server_reports_unsupported_api_version()
    {
        await using var legacy = await LegacyHtmlServer.StartAsync();
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());

        var result = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
        {
            BaseUrl = legacy.BaseUri.ToString(),
        });

        result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Unhealthy);
        result.Stage.ShouldBe(OpenCodeLifecycleStage.HealthProbe);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("unsupported API version");
    }

    [Test]
    public async Task WarmupAsync_when_attach_health_probe_fails_returns_health_probe_stage()
    {
        var port = OpenCodeProcessLauncher.GetFreeTcpPort();
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());

        var result = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
        {
            BaseUrl = $"http://127.0.0.1:{port}/",
        });

        result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Unhealthy);
        result.Stage.ShouldBe(OpenCodeLifecycleStage.HealthProbe);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("health_probe");
        result.ErrorMessage.ShouldContain("connection failed");
    }

    [Test]
    public void BuildRuntimeKey_does_not_expose_the_server_password_and_separates_passwords()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var first = OpenCodeStandaloneServerHost.BuildRuntimeKey(
            new OpenCodeStandaloneServerOptions { BaseUrl = "http://127.0.0.1:4096", ServerPassword = "password-one" },
            environment);
        var second = OpenCodeStandaloneServerHost.BuildRuntimeKey(
            new OpenCodeStandaloneServerOptions { BaseUrl = "http://127.0.0.1:4096", ServerPassword = "password-two" },
            environment);

        first.ShouldNotContain("password-one");
        second.ShouldNotContain("password-two");
        first.ShouldNotBe(second);
    }

    [Test]
    public async Task WarmupAsync_when_owned_runtime_executable_is_missing_returns_owned_runtime_failure()
    {
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        var missingExecutablePath = Path.Combine(Path.GetTempPath(), $"opencode-missing-{Guid.NewGuid():N}.sh");

        var result = await host.WarmupAsync(new OpenCodeStandaloneServerOptions
        {
            ExecutablePath = missingExecutablePath,
            WorkingDirectory = Path.GetTempPath(),
        });

        result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Unhealthy);
        result.Stage.ShouldBe(OpenCodeLifecycleStage.StartOwnedRuntime);
        result.ExecutionProfile.ShouldBe("live");
        result.OwnsRuntime.ShouldBeTrue();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("OpenCode executable was not found");
        result.ErrorMessage.ShouldContain(missingExecutablePath);
        result.DiagnosticOutput.ShouldNotBeNull();
        result.DiagnosticOutput.ShouldContain(missingExecutablePath);
    }

    [Test]
    public async Task AcquireAsync_when_owned_runtime_executable_is_missing_throws_lifecycle_exception()
    {
        await using var host = new OpenCodeStandaloneServerHost(new CliExecutableResolver());
        var missingExecutablePath = Path.Combine(Path.GetTempPath(), $"opencode-missing-{Guid.NewGuid():N}.sh");

        var exception = await Should.ThrowAsync<OpenCodeStandaloneServerLifecycleException>(async () =>
            await host.AcquireAsync(new OpenCodeStandaloneServerOptions
            {
                ExecutablePath = missingExecutablePath,
                WorkingDirectory = Path.GetTempPath(),
            }));

        exception.Result.Status.ShouldBe(OpenCodeStandaloneServerStatus.Unhealthy);
        exception.Result.Stage.ShouldBe(OpenCodeLifecycleStage.StartOwnedRuntime);
        exception.Result.ErrorMessage.ShouldNotBeNull();
        exception.Result.ErrorMessage.ShouldContain("OpenCode executable was not found");
        exception.Result.ErrorMessage.ShouldContain(missingExecutablePath);
    }

    /// <summary>
    /// Writes a shell runtime that prints the OpenCode 2.0 startup lines and keeps running, like <c>opencode serve</c>.
    /// </summary>
    private static string CreateServeShim(Uri baseUri, string? password)
    {
        var fileName = $"opencode-host-shim-{Guid.NewGuid():N}.sh";
        var path = Path.Combine(Path.GetTempPath(), fileName);
        var passwordLine = password is null ? string.Empty : $"printf 'server password {password}\\n'\n";
        var content = $"#!/usr/bin/env bash\nset -euo pipefail\nprintf 'server listening on {baseUri.ToString().TrimEnd('/')}\\n'\n{passwordLine}while true; do sleep 1; done\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>
    /// Answers every request with the Web UI HTML shell and HTTP 200, like a pre-2.0 server.
    /// </summary>
    private sealed class LegacyHtmlServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loopTask;
        private int _disposed;

        private LegacyHtmlServer(HttpListener listener, Uri baseUri, CancellationTokenSource cts, Task loopTask)
        {
            _listener = listener;
            BaseUri = baseUri;
            _cts = cts;
            _loopTask = loopTask;
        }

        public Uri BaseUri { get; }

        public static Task<LegacyHtmlServer> StartAsync()
        {
            var port = OpenCodeProcessLauncher.GetFreeTcpPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var cts = new CancellationTokenSource();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var loopTask = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);
            return Task.FromResult(new LegacyHtmlServer(listener, baseUri, cts, loopTask));
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
                await _loopTask.ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                _cts.Dispose();
            }
        }

        private static async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (context is not null)
                {
                    _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
                }
            }
        }

        private static async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/html; charset=utf-8";
                await WriteStringAsync(context.Response, "<!doctype html><html><body>OpenCode</body></html>").ConfigureAwait(false);
            }
            finally
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    context.Response.Close();
                }
            }
        }

        private static async Task WriteStringAsync(HttpListenerResponse response, string value)
        {
            await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(value).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }
}
