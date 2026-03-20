namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Creates a ready-to-connect IFlow ACP endpoint.
/// </summary>
public interface IIFlowAcpBootstrapper
{
    /// <summary>
    /// Resolves or starts an ACP endpoint for IFlow.
    /// </summary>
    /// <param name="request">The bootstrap request.</param>
    /// <param name="cancellationToken">Cancels the bootstrap flow.</param>
    /// <returns>A bootstrap lease that keeps any managed subprocess alive.</returns>
    Task<IIFlowBootstrapLease> BootstrapAsync(IFlowBootstrapRequest request, CancellationToken cancellationToken = default);
}
