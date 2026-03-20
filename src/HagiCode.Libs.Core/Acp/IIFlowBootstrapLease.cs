namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents a ready IFlow ACP endpoint and any managed resources bound to it.
/// </summary>
public interface IIFlowBootstrapLease : IAsyncDisposable
{
    /// <summary>
    /// Gets the ready ACP endpoint.
    /// </summary>
    Uri Endpoint { get; }

    /// <summary>
    /// Gets a value indicating whether the endpoint was started by the bootstrapper.
    /// </summary>
    bool IsManaged { get; }
}
