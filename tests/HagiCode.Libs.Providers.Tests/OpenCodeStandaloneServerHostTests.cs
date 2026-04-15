using System.Net;
using System.Text;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class OpenCodeStandaloneServerHostTests
{
    [Fact]
    public async Task WarmupAsync_with_live_runtime_returns_ready_diagnostics()
    {
        await using var server = await HealthServer.StartAsync();
        var executablePath = CreateServeShim(server.BaseUri);
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
            result.BaseUri.ShouldBe(server.BaseUri.ToString());
            result.LastSucceededAt.ShouldNotBeNull();
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Fact]
    public async Task AcquireAsync_concurrent_live_requests_collapse_to_one_runtime()
    {
        await using var server = await HealthServer.StartAsync();
        var executablePath = CreateServeShim(server.BaseUri);
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

    [Fact]
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
        result.ErrorMessage.ShouldContain("health_probe");
    }

    [Fact]
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
        result.ErrorMessage.ShouldContain("OpenCode executable was not found");
        result.ErrorMessage.ShouldContain(missingExecutablePath);
        result.DiagnosticOutput.ShouldContain(missingExecutablePath);
    }

    [Fact]
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
        exception.Result.ErrorMessage.ShouldContain("OpenCode executable was not found");
        exception.Result.ErrorMessage.ShouldContain(missingExecutablePath);
    }

    private static string CreateServeShim(Uri baseUri)
    {
        var fileName = $"opencode-host-shim-{Guid.NewGuid():N}.sh";
        var path = Path.Combine(Path.GetTempPath(), fileName);
        var content = $"#!/usr/bin/env bash\nset -euo pipefail\nprintf 'opencode server listening on {baseUri}\\n'\nwhile true; do sleep 1; done\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class HealthServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loopTask;
        private int _disposed;

        private HealthServer(HttpListener listener, Uri baseUri, CancellationTokenSource cts, Task loopTask)
        {
            _listener = listener;
            BaseUri = baseUri;
            _cts = cts;
            _loopTask = loopTask;
        }

        public Uri BaseUri { get; }

        public static async Task<HealthServer> StartAsync()
        {
            var port = OpenCodeProcessLauncher.GetFreeTcpPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var cts = new CancellationTokenSource();
            var server = new HealthServer(listener, new Uri($"http://127.0.0.1:{port}/"), cts, Task.CompletedTask);
            var loopTask = Task.Run(() => server.AcceptLoopAsync(cts.Token), CancellationToken.None);
            await Task.Yield();
            return new HealthServer(listener, server.BaseUri, cts, loopTask);
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

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
                }
            }
        }

        private static async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/global/health")
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await WriteStringAsync(context.Response, "{\"healthy\":true,\"version\":\"host-test-1.0.0\"}", cancellationToken).ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = 404;
                await WriteStringAsync(context.Response, "Not Found", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    context.Response.Close();
                }
            }
        }

        private static async Task WriteStringAsync(HttpListenerResponse response, string value, CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
