using System.Diagnostics;
using HagiCode.Libs.Core.Process;
using Microsoft.Extensions.Logging;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Registers a Codex child process (exec or app-server) with the shared process ledger under the caller's session id, so
/// the session's process tree can find it. The process is started by the Codex transport and is not stopped by the
/// registration. A failed registration is logged and the run continues without a visible process tree.
/// </summary>
internal static class CodexProcessLedger
{
    /// <summary>The provider name the ledger records for Codex processes.</summary>
    internal const string ProviderName = "codex";

    /// <summary>
    /// Registers <paramref name="process"/> when both a ledger and an owner session id are supplied. Dispose the returned
    /// registration to remove the ledger entry; it does not stop the process. Returns <see langword="null"/> when nothing was
    /// registered.
    /// </summary>
    internal static async Task<CliExternalProcessRegistration?> TryRegisterAsync(
        CliProcessManager? processManager,
        string? ownerSessionId,
        Process process,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sessionId = ArgumentValueNormalizer.NormalizeOptionalValue(ownerSessionId);
        if (processManager is null || sessionId is null)
        {
            return null;
        }

        try
        {
            return await processManager.RegisterExternalProcessAsync(
                    process,
                    new CliExternalProcessRequest
                    {
                        ProviderName = ProviderName,
                        SessionId = sessionId,
                        ExecutablePath = process.StartInfo.FileName,
                        WorkingDirectory = process.StartInfo.WorkingDirectory,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not register a Codex process with the process ledger; the session runs without its process tree.");
            return null;
        }
    }
}
