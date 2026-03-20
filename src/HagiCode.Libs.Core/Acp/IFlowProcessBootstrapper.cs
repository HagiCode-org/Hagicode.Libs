using System.Net.Sockets;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Starts managed IFlow ACP processes or reuses explicit ACP endpoints.
/// </summary>
public class IFlowProcessBootstrapper : IIFlowAcpBootstrapper
{
    private static readonly TimeSpan EndpointProbeDelay = TimeSpan.FromMilliseconds(150);
    private readonly CliProcessManager _processManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="IFlowProcessBootstrapper"/> class.
    /// </summary>
    /// <param name="processManager">The process manager.</param>
    public IFlowProcessBootstrapper(CliProcessManager processManager)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
    }

    /// <inheritdoc />
    public async Task<IIFlowBootstrapLease> BootstrapAsync(IFlowBootstrapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Endpoint is not null)
        {
            return new IFlowBootstrapLease(NormalizeEndpoint(request.Endpoint), managedHandle: null, _processManager);
        }

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            throw new InvalidOperationException(
                "IFlow ACP bootstrap failed during executable discovery: no executable path was supplied for managed startup.");
        }

        var endpoint = CreateManagedEndpointUri(AllocatePort());
        var startContext = new ProcessStartContext
        {
            ExecutablePath = request.ExecutablePath,
            Arguments = BuildManagedArguments(endpoint.Port, request.Arguments),
            WorkingDirectory = request.WorkingDirectory,
            EnvironmentVariables = request.EnvironmentVariables
        };

        CliProcessHandle? handle = null;
        try
        {
            handle = await _processManager.StartAsync(startContext, cancellationToken).ConfigureAwait(false);
            await WaitForEndpointReadyAsync(endpoint, handle, request.StartupTimeout, cancellationToken).ConfigureAwait(false);
            return new IFlowBootstrapLease(endpoint, handle, _processManager);
        }
        catch (Exception ex)
        {
            if (handle is not null)
            {
                await _processManager.StopAsync(handle, CancellationToken.None).ConfigureAwait(false);
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException($"IFlow ACP bootstrap failed during managed startup: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Allocates a local TCP port for the managed endpoint.
    /// </summary>
    /// <returns>The allocated port.</returns>
    protected virtual int AllocatePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Probes the endpoint until it becomes reachable or the timeout elapses.
    /// </summary>
    /// <param name="endpoint">The endpoint to probe.</param>
    /// <param name="processHandle">The managed process handle.</param>
    /// <param name="startupTimeout">The startup timeout.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    protected virtual async Task WaitForEndpointReadyAsync(
        Uri endpoint,
        CliProcessHandle processHandle,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(startupTimeout);

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();

            if (processHandle.Process.HasExited)
            {
                var standardError = await processHandle.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                var detail = string.IsNullOrWhiteSpace(standardError)
                    ? $"Process exited with code {processHandle.Process.ExitCode} before endpoint {endpoint} became ready."
                    : $"Process exited with code {processHandle.Process.ExitCode} before endpoint {endpoint} became ready. Stderr: {standardError.Trim()}";
                throw new InvalidOperationException(detail);
            }

            if (await IsEndpointReadyAsync(endpoint, timeoutCts.Token).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(EndpointProbeDelay, timeoutCts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Probes a TCP endpoint for readiness.
    /// </summary>
    /// <param name="endpoint">The endpoint to probe.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns><see langword="true"/> when the endpoint is reachable; otherwise <see langword="false"/>.</returns>
    protected virtual async Task<bool> IsEndpointReadyAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.DnsSafeHost, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Uri NormalizeEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException("IFlow ACP endpoint must be an absolute URI.");
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("IFlow ACP endpoint must use the ws:// or wss:// scheme.");
        }

        return endpoint;
    }

    private static Uri CreateManagedEndpointUri(int port)
    {
        return new Uri($"ws://127.0.0.1:{port}/acp", UriKind.Absolute);
    }

    private static IReadOnlyList<string> BuildManagedArguments(int port, IReadOnlyList<string> additionalArguments)
    {
        var arguments = new List<string> { "--experimental-acp", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var argument in additionalArguments)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (string.Equals(argument, "--experimental-acp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--port", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            arguments.Add(argument);
        }

        return arguments;
    }

    private sealed class IFlowBootstrapLease : IIFlowBootstrapLease
    {
        private readonly CliProcessHandle? _managedHandle;
        private readonly CliProcessManager _processManager;
        private int _disposed;

        public IFlowBootstrapLease(Uri endpoint, CliProcessHandle? managedHandle, CliProcessManager processManager)
        {
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _managedHandle = managedHandle;
            _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        }

        public Uri Endpoint { get; }

        public bool IsManaged => _managedHandle is not null;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_managedHandle is not null)
            {
                await _processManager.StopAsync(_managedHandle, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
