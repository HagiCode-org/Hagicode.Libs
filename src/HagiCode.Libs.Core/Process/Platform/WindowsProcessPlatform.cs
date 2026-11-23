using System.ComponentModel;
using System.Runtime.InteropServices;
using HagiCode.Libs.Core.Process.Tree;

namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// Reads processes on Windows. Parent links and names come from a Toolhelp snapshot. Details are read with limited-query access, so processes the user can open at Medium integrity level are readable.
/// </summary>
internal sealed class WindowsProcessPlatform : IProcessPlatform
{
    private const int CommandLineBufferBytes = 32 * 1024;

    /// <inheritdoc />
    public IReadOnlyList<ProcessTableRow> ReadProcessTable()
    {
        var rows = new List<ProcessTableRow>();
        var snapshot = WindowsNative.CreateToolhelp32Snapshot(WindowsNative.TH32CsSnapProcess, 0);
        if (snapshot == WindowsNative.InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to take a process snapshot.");
        }

        try
        {
            var entry = new WindowsNative.ProcessEntry32 { Size = (uint)Marshal.SizeOf<WindowsNative.ProcessEntry32>() };
            if (!WindowsNative.Process32FirstW(snapshot, ref entry))
            {
                return rows;
            }

            do
            {
                rows.Add(new ProcessTableRow((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }
            while (WindowsNative.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            WindowsNative.CloseHandle(snapshot);
        }

        return rows;
    }

    /// <inheritdoc />
    public ProcessDetails? ReadDetails(int pid, bool includeCommandLine)
    {
        if (pid <= 0)
        {
            return null;
        }

        var handle = WindowsNative.OpenProcess(WindowsNative.ProcessQueryLimitedInformation, inheritHandle: false, (uint)pid);
        if (handle == 0)
        {
            // Invalid parameter means no process has this id. Any other failure, typically access denied, leaves the identity unknown.
            return Marshal.GetLastPInvokeError() == WindowsNative.ErrorInvalidParameter
                ? null
                : Denied();
        }

        try
        {
            if (WindowsNative.GetExitCodeProcess(handle, out var exitCode) && exitCode != WindowsNative.StillActive)
            {
                return null;
            }

            if (!WindowsNative.GetProcessTimes(handle, out var creationTime, out _, out var kernelTime, out var userTime)
                || creationTime <= 0)
            {
                return Denied();
            }

            var startedAtUtc = new DateTimeOffset(DateTime.FromFileTime(creationTime).ToUniversalTime(), TimeSpan.Zero);
            var cpuTime = TimeSpan.FromTicks(kernelTime + userTime);

            var accessibility = ProcessAccessibility.Accessible;
            long? workingSet = null;
            var counters = new WindowsNative.ProcessMemoryCounters();
            if (WindowsNative.GetProcessMemoryInfo(handle, out counters, (uint)Marshal.SizeOf<WindowsNative.ProcessMemoryCounters>()))
            {
                workingSet = counters.WorkingSetSize;
            }
            else
            {
                accessibility = ProcessAccessibility.Partial;
            }

            string? commandLine = null;
            if (includeCommandLine)
            {
                commandLine = TryReadCommandLine(handle);
                if (commandLine is null)
                {
                    accessibility = ProcessAccessibility.Partial;
                }
            }

            return new ProcessDetails(startedAtUtc, cpuTime, workingSet, commandLine, Name: null, accessibility);
        }
        finally
        {
            WindowsNative.CloseHandle(handle);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<int> ReadContainmentMembers(int rootPid)
    {
        // Job membership belongs to the managed-process ledger, which holds the Job handles. The platform has no containment of its own.
        return [];
    }

    /// <inheritdoc />
    public ProcessKillResult Kill(int pid)
    {
        var handle = WindowsNative.OpenProcess(WindowsNative.ProcessTerminate | WindowsNative.ProcessQueryLimitedInformation, inheritHandle: false, (uint)pid);
        if (handle == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error switch
            {
                WindowsNative.ErrorInvalidParameter => new ProcessKillResult(ProcessKillStatus.AlreadyExited, "The process no longer exists."),
                WindowsNative.ErrorAccessDenied => new ProcessKillResult(ProcessKillStatus.AccessDenied, "Access to the process was denied."),
                _ => new ProcessKillResult(ProcessKillStatus.Failed, new Win32Exception(error).Message),
            };
        }

        try
        {
            if (WindowsNative.TerminateProcess(handle, exitCode: 1))
            {
                return new ProcessKillResult(ProcessKillStatus.Killed);
            }

            var error = Marshal.GetLastPInvokeError();
            if (WindowsNative.GetExitCodeProcess(handle, out var exitCode) && exitCode != WindowsNative.StillActive)
            {
                return new ProcessKillResult(ProcessKillStatus.AlreadyExited, "The process exited before it could be terminated.");
            }

            return error == WindowsNative.ErrorAccessDenied
                ? new ProcessKillResult(ProcessKillStatus.AccessDenied, "Access to the process was denied.")
                : new ProcessKillResult(ProcessKillStatus.Failed, new Win32Exception(error).Message);
        }
        finally
        {
            WindowsNative.CloseHandle(handle);
        }
    }

    private static ProcessDetails Denied() => new(null, null, null, null, null, ProcessAccessibility.Denied);

    private static string? TryReadCommandLine(nint process)
    {
        var buffer = Marshal.AllocHGlobal(CommandLineBufferBytes);
        try
        {
            var status = WindowsNative.NtQueryInformationProcess(process, WindowsNative.ProcessCommandLineInformation, buffer, CommandLineBufferBytes, out _);
            if (status != 0)
            {
                return null;
            }

            var unicode = Marshal.PtrToStructure<WindowsNative.UnicodeString>(buffer);
            if (unicode.Length == 0 || unicode.Buffer == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
