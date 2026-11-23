using System.ComponentModel;
using System.Runtime.InteropServices;
using HagiCode.Libs.Core.Process.Platform;

namespace HagiCode.Libs.Core.Process.Windows;

/// <summary>
/// Creates Job Objects with the kill-on-close limit on Windows.
/// </summary>
internal sealed class WindowsJobFactory : IWindowsJobFactory
{
    /// <summary>
    /// Gets the shared factory.
    /// </summary>
    public static IWindowsJobFactory Default { get; } = new WindowsJobFactory();

    /// <inheritdoc />
    public IWindowsProcessJob Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job Objects are only available on Windows.");
        }

        var job = WindowsJobNative.CreateJobObject(nint.Zero, null);
        if (job == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create a Job Object.");
        }

        try
        {
            var info = new WindowsJobNative.ExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = WindowsJobNative.JobObjectLimitKillOnJobClose;
            if (!WindowsJobNative.SetInformationJobObject(job, WindowsJobNative.JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<WindowsJobNative.ExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to configure the Job Object.");
            }

            return new WindowsProcessJob(job);
        }
        catch
        {
            WindowsNative.CloseHandle(job);
            throw;
        }
    }
}

/// <summary>
/// One Job Object. Closing it ends every member that is still running.
/// </summary>
internal sealed class WindowsProcessJob : IWindowsProcessJob
{
    private const int MaxListAttempts = 8;
    private const int ListPadding = 16;

    private nint _handle;

    /// <summary>
    /// Wraps an open Job handle. The job takes ownership of the handle.
    /// </summary>
    /// <param name="handle">The Job handle.</param>
    public WindowsProcessJob(nint handle)
    {
        _handle = handle;
    }

    /// <inheritdoc />
    public bool TryAssignProcess(int pid, out int win32Error)
    {
        ThrowIfDisposed();
        win32Error = 0;
        var process = WindowsNative.OpenProcess(
            WindowsNative.ProcessSetQuota | WindowsNative.ProcessTerminate | WindowsNative.ProcessQueryLimitedInformation,
            inheritHandle: false,
            (uint)pid);
        if (process == nint.Zero)
        {
            win32Error = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            if (WindowsJobNative.AssignProcessToJobObject(_handle, process))
            {
                return true;
            }

            win32Error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            WindowsNative.CloseHandle(process);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<int> ListMemberProcessIds()
    {
        ThrowIfDisposed();
        var capacity = ListPadding;
        for (var attempt = 0; attempt < MaxListAttempts; attempt++)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORDs, then one ULONG_PTR per process id.
            var size = 8 + (capacity * nint.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!WindowsJobNative.QueryInformationJobObject(_handle, WindowsJobNative.JobObjectBasicProcessIdList, buffer, (uint)size, out _))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != WindowsJobNative.ErrorMoreData)
                    {
                        throw new Win32Exception(error, "Failed to list the members of the Job Object.");
                    }
                }

                var assigned = Marshal.ReadInt32(buffer, 0);
                var listed = Marshal.ReadInt32(buffer, 4);
                if (assigned > listed && listed >= capacity)
                {
                    capacity = assigned + ListPadding;
                    continue;
                }

                var pids = new List<int>(listed);
                for (var index = 0; index < listed; index++)
                {
                    pids.Add((int)Marshal.ReadIntPtr(buffer, 8 + (index * nint.Size)));
                }

                return pids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("The Job Object membership list kept changing while it was read.");
    }

    /// <inheritdoc />
    public void Terminate()
    {
        ThrowIfDisposed();
        if (!WindowsJobNative.TerminateJobObject(_handle, exitCode: 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to terminate the Job Object.");
        }
    }

    /// <inheritdoc />
    public void ReleaseKeepingMembers()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        var info = new WindowsJobNative.ExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = 0;
        WindowsJobNative.SetInformationJobObject(_handle, WindowsJobNative.JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<WindowsJobNative.ExtendedLimitInformation>());
        Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        WindowsNative.CloseHandle(_handle);
        _handle = nint.Zero;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
    }
}

/// <summary>
/// Brings the descendants a process created before it was assigned into its Job.
/// </summary>
internal static class WindowsJobSweep
{
    /// <summary>
    /// Walks the parent links below the root and assigns every descendant that started no earlier than its parent.
    /// </summary>
    /// <param name="job">The Job the root belongs to.</param>
    /// <param name="platform">The process platform used to read the table and details.</param>
    /// <param name="rootPid">The root process id.</param>
    /// <param name="rootStartedAtUtc">The root start time.</param>
    /// <param name="startTimeTolerance">The start-time tolerance used by the reuse guard.</param>
    /// <param name="onFailure">Called with the process id and Win32 error for each descendant that could not be assigned.</param>
    /// <returns>The number of descendants that joined the Job.</returns>
    public static int Sweep(
        IWindowsProcessJob job,
        IProcessPlatform platform,
        int rootPid,
        DateTimeOffset rootStartedAtUtc,
        TimeSpan startTimeTolerance,
        Action<int, int> onFailure)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var row in platform.ReadProcessTable())
        {
            if (row.ParentPid == row.Pid)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(row.ParentPid, out var children))
            {
                children = [];
                childrenByParent[row.ParentPid] = children;
            }

            children.Add(row.Pid);
        }

        var assigned = 0;
        var visited = new HashSet<int> { rootPid };
        var queue = new Queue<(int Pid, DateTimeOffset StartedAtUtc)>();
        queue.Enqueue((rootPid, rootStartedAtUtc));
        while (queue.Count > 0)
        {
            var (parentPid, parentStart) = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parentPid, out var children))
            {
                continue;
            }

            foreach (var childPid in children)
            {
                if (!visited.Add(childPid))
                {
                    continue;
                }

                var details = platform.ReadDetails(childPid, includeCommandLine: false);
                if (details?.StartedAtUtc is not { } childStart || childStart < parentStart - startTimeTolerance)
                {
                    continue;
                }

                if (job.TryAssignProcess(childPid, out var win32Error))
                {
                    assigned++;
                    queue.Enqueue((childPid, childStart));
                }
                else
                {
                    onFailure(childPid, win32Error);
                }
            }
        }

        return assigned;
    }
}
