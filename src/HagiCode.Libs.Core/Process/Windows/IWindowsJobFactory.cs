namespace HagiCode.Libs.Core.Process.Windows;

/// <summary>
/// Creates Windows Job Objects. The seam lets tests force each failure without a real Job.
/// </summary>
internal interface IWindowsJobFactory
{
    /// <summary>
    /// Creates a Job Object whose members end when the Job is closed.
    /// </summary>
    /// <returns>The new Job.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The Job could not be created or configured.</exception>
    IWindowsProcessJob Create();
}

/// <summary>
/// One Windows Job Object that holds a managed process and its descendants.
/// </summary>
internal interface IWindowsProcessJob : IDisposable
{
    /// <summary>
    /// Tries to assign a live process to the Job by its id. The Job opens the process with set-quota and terminate access.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <param name="win32Error">The Win32 error code when the assignment failed.</param>
    /// <returns><see langword="true" /> when the process joined the Job.</returns>
    bool TryAssignProcess(int pid, out int win32Error);

    /// <summary>
    /// Lists the process ids that are currently members of the Job.
    /// </summary>
    IReadOnlyList<int> ListMemberProcessIds();

    /// <summary>
    /// Ends every member of the Job.
    /// </summary>
    void Terminate();

    /// <summary>
    /// Closes the Job without ending its members. The kill-on-close limit is cleared first, so closing does not end anything.
    /// </summary>
    void ReleaseKeepingMembers();
}
