using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;

namespace HagiCode.Libs.Providers.OpenCode;

public sealed class OpenCodeStandaloneServerHost : IOpenCodeStandaloneServerClient
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyRuntimeEnvironment =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<Task<OpenCodeStandaloneServerConnection>>> _runtimeCache =
        new(StringComparer.Ordinal);
    private readonly CliExecutableResolver _executableResolver;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly OpenCodeProcessLauncher _processLauncher;
    private int _disposed;

    public OpenCodeStandaloneServerHost(
        CliExecutableResolver executableResolver,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
        : this(executableResolver, runtimeEnvironmentResolver, null)
    {
    }

    internal OpenCodeStandaloneServerHost(
        CliExecutableResolver executableResolver,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver,
        OpenCodeProcessLauncher? processLauncher)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _processLauncher = processLauncher ?? new OpenCodeProcessLauncher(_executableResolver);
    }

    public async Task<OpenCodeStandaloneServerConnection> AcquireAsync(
        OpenCodeStandaloneServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var runtimeKey = BuildRuntimeKey(options, runtimeEnvironment);
        while (true)
        {
            var lazyRuntime = _runtimeCache.GetOrAdd(
                runtimeKey,
                _ => new Lazy<Task<OpenCodeStandaloneServerConnection>>(
                    () => CreateRuntimeAsync(options, runtimeEnvironment, runtimeKey, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                var runtime = await lazyRuntime.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                await ProbeHealthyAsync(runtime, options, runtimeKey, cancellationToken).ConfigureAwait(false);
                return runtime;
            }
            catch
            {
                if (_runtimeCache.TryRemove(runtimeKey, out var removed) && removed.IsValueCreated)
                {
                    try
                    {
                        var runtime = await removed.Value.ConfigureAwait(false);
                        await runtime.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                throw;
            }
        }
    }

    public async Task<OpenCodeStandaloneServerLifecycleResult> WarmupAsync(
        OpenCodeStandaloneServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var runtime = await AcquireAsync(options, cancellationToken).ConfigureAwait(false);
            return runtime.LifecycleResult;
        }
        catch (OpenCodeStandaloneServerLifecycleException ex)
        {
            return ex.Result;
        }
    }

    public async Task<OpenCodeStandaloneServerLifecycleResult> InvalidateAsync(
        OpenCodeStandaloneServerOptions options,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var runtimeKey = BuildRuntimeKey(options, runtimeEnvironment);
        var attemptedAt = DateTimeOffset.UtcNow;
        var workingDirectory = NormalizeWorkingDirectory(options.WorkingDirectory);

        if (!_runtimeCache.TryRemove(runtimeKey, out var lazyRuntime))
        {
            return new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Unhealthy,
                Stage = OpenCodeLifecycleStage.Invalidated,
                RuntimeKey = runtimeKey,
                WorkingDirectory = workingDirectory,
                ErrorMessage = NormalizeReason(reason, "OpenCode runtime invalidated before it was initialized."),
                AttemptedAt = attemptedAt,
            };
        }

        if (!lazyRuntime.IsValueCreated)
        {
            return new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Unhealthy,
                Stage = OpenCodeLifecycleStage.Invalidated,
                RuntimeKey = runtimeKey,
                WorkingDirectory = workingDirectory,
                ErrorMessage = NormalizeReason(reason, "OpenCode runtime invalidated before it finished initializing."),
                AttemptedAt = attemptedAt,
            };
        }

        OpenCodeStandaloneServerConnection? runtime = null;
        OpenCodeStandaloneServerLifecycleResult previous = new()
        {
            Status = OpenCodeStandaloneServerStatus.Unhealthy,
            Stage = OpenCodeLifecycleStage.Invalidated,
            RuntimeKey = runtimeKey,
            WorkingDirectory = workingDirectory,
            AttemptedAt = attemptedAt,
        };

        try
        {
            runtime = await lazyRuntime.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            previous = runtime.LifecycleResult;
        }
        catch (OpenCodeStandaloneServerLifecycleException ex)
        {
            previous = ex.Result;
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new OpenCodeStandaloneServerLifecycleResult
        {
            Status = OpenCodeStandaloneServerStatus.Unhealthy,
            Stage = OpenCodeLifecycleStage.Invalidated,
            RuntimeKey = runtimeKey,
            BaseUri = previous.BaseUri,
            Version = previous.Version,
            ExecutionProfile = previous.ExecutionProfile,
            OwnsRuntime = previous.OwnsRuntime,
            WorkingDirectory = previous.WorkingDirectory ?? workingDirectory,
            AttemptedAt = attemptedAt,
            LastSucceededAt = previous.LastSucceededAt,
            ErrorMessage = NormalizeReason(reason, "OpenCode runtime invalidated by caller."),
            DiagnosticOutput = previous.DiagnosticOutput,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var entry in _runtimeCache.Values)
        {
            if (!entry.IsValueCreated)
            {
                continue;
            }

            try
            {
                var runtime = await entry.Value.ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _runtimeCache.Clear();
    }

    internal static string BuildRuntimeKey(
        OpenCodeStandaloneServerOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeEnvironment);

        var builder = new StringBuilder();
        builder.Append("baseUrl=").Append(Normalize(options.BaseUrl)).Append('|');
        builder.Append("executable=").Append(Normalize(options.ExecutablePath)).Append('|');
        builder.Append("workingDirectory=").Append(Normalize(NormalizeWorkingDirectory(options.WorkingDirectory))).Append('|');
        builder.Append("workspace=").Append(Normalize(options.Workspace)).Append('|');
        builder.Append("extraArgs=").Append(string.Join(',', options.ExtraArguments.Select(static value => Normalize(value)))).Append('|');

        foreach (var pair in runtimeEnvironment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("env:").Append(pair.Key).Append('=').Append(Normalize(pair.Value)).Append('|');
        }

        foreach (var pair in options.EnvironmentVariables.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("override:").Append(pair.Key).Append('=').Append(Normalize(pair.Value)).Append('|');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private async Task<OpenCodeStandaloneServerConnection> CreateRuntimeAsync(
        OpenCodeStandaloneServerOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        string runtimeKey,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var workingDirectory = NormalizeWorkingDirectory(options.WorkingDirectory);
        var requestTimeout = options.RequestTimeout ?? TimeSpan.FromMinutes(3);
        var stage = OpenCodeLifecycleStage.Attach;
        OpenCodeStandaloneServerConnection? runtime = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                stage = OpenCodeLifecycleStage.Attach;
                if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var baseUri))
                {
                    throw CreateLifecycleException(
                        OpenCodeStandaloneServerStatus.Unhealthy,
                        stage,
                        runtimeKey,
                        executionProfile: "attach",
                        ownsRuntime: false,
                        workingDirectory,
                        attemptedAt,
                        errorMessage: $"OpenCode base URL '{options.BaseUrl}' is not a valid absolute URI.");
                }

                runtime = CreateHandle(baseUri, options, runtimeKey, requestTimeout, "attach", ownsRuntime: false, ownedResource: null, attemptedAt, diagnosticOutput: null);
                await ProbeHealthyAsync(runtime, options, runtimeKey, cancellationToken).ConfigureAwait(false);
                return runtime;
            }

            stage = OpenCodeLifecycleStage.StartOwnedRuntime;
            var executablePath = _processLauncher.ResolveExecutablePath(options, runtimeEnvironment);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw CreateMissingExecutableLifecycleException(
                    options,
                    runtimeKey,
                    workingDirectory,
                    attemptedAt,
                    executablePath);
            }

            var process = await _processLauncher.StartAsync(executablePath, options, runtimeEnvironment, cancellationToken).ConfigureAwait(false);
            runtime = CreateHandle(process.BaseUri, options, runtimeKey, requestTimeout, "live", ownsRuntime: true, process, attemptedAt, process.CapturedOutput);
            await ProbeHealthyAsync(runtime, options, runtimeKey, cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (OpenCodeStandaloneServerLifecycleException)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            throw CreateLifecycleException(
                OpenCodeStandaloneServerStatus.Unhealthy,
                stage,
                runtimeKey,
                executionProfile: stage == OpenCodeLifecycleStage.Attach ? "attach" : "live",
                ownsRuntime: stage != OpenCodeLifecycleStage.Attach,
                workingDirectory,
                attemptedAt,
                errorMessage: $"OpenCode lifecycle stage '{FormatStage(stage)}' failed: {ex.Message}",
                diagnosticOutput: ex is TimeoutException or InvalidOperationException ? ex.Message : null,
                baseUri: runtime?.BaseUri.ToString());
        }
    }

    private static async Task ProbeHealthyAsync(
        OpenCodeStandaloneServerConnection runtime,
        OpenCodeStandaloneServerOptions options,
        string runtimeKey,
        CancellationToken cancellationToken)
    {
        var attemptedAt = runtime.LifecycleResult.AttemptedAt;
        var workingDirectory = runtime.LifecycleResult.WorkingDirectory ?? NormalizeWorkingDirectory(options.WorkingDirectory);

        try
        {
            var health = await runtime.Client.HealthAsync(cancellationToken).ConfigureAwait(false);
            if (!health.Healthy)
            {
                throw CreateLifecycleException(
                    OpenCodeStandaloneServerStatus.Unhealthy,
                    OpenCodeLifecycleStage.HealthProbe,
                    runtimeKey,
                    runtime.ExecutionProfile,
                    runtime.OwnsRuntime,
                    workingDirectory,
                    attemptedAt,
                    errorMessage: "OpenCode lifecycle stage 'health_probe' failed: OpenCode health endpoint reported an unhealthy runtime.",
                    diagnosticOutput: runtime.LifecycleResult.DiagnosticOutput,
                    baseUri: runtime.BaseUri.ToString());
            }

            runtime.LifecycleResult = new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Ready,
                Stage = OpenCodeLifecycleStage.Ready,
                RuntimeKey = runtimeKey,
                BaseUri = runtime.BaseUri.ToString(),
                Version = string.IsNullOrWhiteSpace(health.Version) ? null : health.Version,
                ExecutionProfile = runtime.ExecutionProfile,
                OwnsRuntime = runtime.OwnsRuntime,
                WorkingDirectory = workingDirectory,
                AttemptedAt = attemptedAt,
                LastSucceededAt = runtime.LifecycleResult.LastSucceededAt ?? DateTimeOffset.UtcNow,
                DiagnosticOutput = runtime.LifecycleResult.DiagnosticOutput,
            };
        }
        catch (OpenCodeStandaloneServerLifecycleException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateLifecycleException(
                OpenCodeStandaloneServerStatus.Unhealthy,
                OpenCodeLifecycleStage.HealthProbe,
                runtimeKey,
                runtime.ExecutionProfile,
                runtime.OwnsRuntime,
                workingDirectory,
                attemptedAt,
                errorMessage: $"OpenCode lifecycle stage 'health_probe' failed: {ex.Message}",
                diagnosticOutput: runtime.LifecycleResult.DiagnosticOutput,
                baseUri: runtime.BaseUri.ToString());
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return EmptyRuntimeEnvironment;
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OpenCodeStandaloneServerConnection CreateHandle(
        Uri baseUri,
        OpenCodeStandaloneServerOptions options,
        string runtimeKey,
        TimeSpan requestTimeout,
        string executionProfile,
        bool ownsRuntime,
        IAsyncDisposable? ownedResource,
        DateTimeOffset attemptedAt,
        string? diagnosticOutput)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = NormalizeBaseUri(baseUri),
            Timeout = requestTimeout,
        };
        var client = new OpenCodeHttpClient(httpClient, options.WorkingDirectory, options.Workspace);
        return new OpenCodeStandaloneServerConnection(
            runtimeKey,
            client,
            executionProfile,
            ownsRuntime,
            ownedResource,
            new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Initializing,
                Stage = executionProfile == "attach" ? OpenCodeLifecycleStage.Attach : OpenCodeLifecycleStage.StartOwnedRuntime,
                RuntimeKey = runtimeKey,
                BaseUri = NormalizeBaseUri(baseUri).ToString(),
                ExecutionProfile = executionProfile,
                OwnsRuntime = ownsRuntime,
                WorkingDirectory = NormalizeWorkingDirectory(options.WorkingDirectory),
                AttemptedAt = attemptedAt,
                DiagnosticOutput = string.IsNullOrWhiteSpace(diagnosticOutput) ? null : diagnosticOutput,
            });
    }

    private static OpenCodeStandaloneServerLifecycleException CreateLifecycleException(
        OpenCodeStandaloneServerStatus status,
        OpenCodeLifecycleStage stage,
        string runtimeKey,
        string executionProfile,
        bool ownsRuntime,
        string? workingDirectory,
        DateTimeOffset attemptedAt,
        string errorMessage,
        string? diagnosticOutput = null,
        string? baseUri = null)
    {
        return new OpenCodeStandaloneServerLifecycleException(
            new OpenCodeStandaloneServerLifecycleResult
            {
                Status = status,
                Stage = stage,
                RuntimeKey = runtimeKey,
                BaseUri = baseUri,
                ExecutionProfile = executionProfile,
                OwnsRuntime = ownsRuntime,
                WorkingDirectory = workingDirectory,
                AttemptedAt = attemptedAt,
                ErrorMessage = errorMessage,
                DiagnosticOutput = string.IsNullOrWhiteSpace(diagnosticOutput) ? null : diagnosticOutput,
            });
    }

    private static OpenCodeStandaloneServerLifecycleException CreateMissingExecutableLifecycleException(
        OpenCodeStandaloneServerOptions options,
        string runtimeKey,
        string? workingDirectory,
        DateTimeOffset attemptedAt,
        string? resolvedExecutablePath)
    {
        var configuredExecutable = string.IsNullOrWhiteSpace(options.ExecutablePath)
            ? "opencode"
            : options.ExecutablePath.Trim();
        var resolvedDisplay = string.IsNullOrWhiteSpace(resolvedExecutablePath)
            ? "(unresolved)"
            : resolvedExecutablePath.Trim();
        var workingDirectoryDisplay = workingDirectory ?? "(none)";
        var errorMessage =
            $"OpenCode lifecycle stage 'owned_runtime' failed: OpenCode executable was not found. Configured executable='{configuredExecutable}', resolved executable='{resolvedDisplay}', workingDirectory='{workingDirectoryDisplay}'.";

        return CreateLifecycleException(
            OpenCodeStandaloneServerStatus.Unhealthy,
            OpenCodeLifecycleStage.StartOwnedRuntime,
            runtimeKey,
            executionProfile: "live",
            ownsRuntime: true,
            workingDirectory,
            attemptedAt,
            errorMessage,
            diagnosticOutput: errorMessage);
    }

    private static string FormatStage(OpenCodeLifecycleStage stage)
    {
        return stage switch
        {
            OpenCodeLifecycleStage.Attach => "attach",
            OpenCodeLifecycleStage.StartOwnedRuntime => "owned_runtime",
            OpenCodeLifecycleStage.HealthProbe => "health_probe",
            OpenCodeLifecycleStage.Invalidated => "invalidated",
            OpenCodeLifecycleStage.Ready => "ready",
            OpenCodeLifecycleStage.Skipped => "skipped",
            _ => "none",
        };
    }

    private static Uri NormalizeBaseUri(Uri input)
    {
        var text = input.ToString();
        return text.EndsWith("/", StringComparison.Ordinal) ? input : new Uri(text + "/");
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(workingDirectory);
        }
        catch
        {
            return workingDirectory.Trim();
        }
    }

    private static string NormalizeReason(string? reason, string fallback)
    {
        return string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(OpenCodeStandaloneServerHost));
        }
    }
}
