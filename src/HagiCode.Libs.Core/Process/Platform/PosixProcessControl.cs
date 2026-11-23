using System.ComponentModel;

namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// Ends a process on a POSIX system (Linux and macOS) and maps the error numbers to typed results.
/// </summary>
internal static class PosixProcessControl
{
    private const int EPerm = 1;
    private const int ESrch = 3;

    /// <summary>
    /// Sends SIGKILL to one process by id. The caller must have verified its identity first.
    /// </summary>
    /// <param name="pid">The process id.</param>
    public static ProcessKillResult Kill(int pid)
    {
        try
        {
            System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: false);
            return new ProcessKillResult(ProcessKillStatus.Killed);
        }
        catch (ArgumentException)
        {
            return new ProcessKillResult(ProcessKillStatus.AlreadyExited, "The process no longer exists.");
        }
        catch (InvalidOperationException)
        {
            return new ProcessKillResult(ProcessKillStatus.AlreadyExited, "The process no longer exists.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == EPerm)
        {
            return new ProcessKillResult(ProcessKillStatus.AccessDenied, ex.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ESrch)
        {
            return new ProcessKillResult(ProcessKillStatus.AlreadyExited, ex.Message);
        }
        catch (Win32Exception ex)
        {
            return new ProcessKillResult(ProcessKillStatus.Failed, ex.Message);
        }
    }
}
