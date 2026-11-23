namespace HagiCode.Libs.Core.Process.Platform;

/// <summary>
/// Chooses the process platform for the running operating system once.
/// </summary>
internal static class ProcessPlatformSelector
{
    private static readonly Lazy<IProcessPlatform> Current = new(Create);

    /// <summary>
    /// Gets the platform for the running operating system.
    /// </summary>
    public static IProcessPlatform Default => Current.Value;

    private static IProcessPlatform Create()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxProcessPlatform();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsProcessPlatform();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsProcessPlatform();
        }

        return new UnsupportedProcessPlatform();
    }
}

/// <summary>
/// Stands in on an operating system without a process platform. Every read fails with a clear unsupported-platform error.
/// </summary>
internal sealed class UnsupportedProcessPlatform : IProcessPlatform
{
    /// <summary>
    /// The message used when a snapshot or termination is requested on an unsupported platform.
    /// </summary>
    public const string UnsupportedMessage = "Process tree snapshots and identity-checked termination are not supported on this platform. Supported platforms are Linux, Windows and macOS.";

    /// <inheritdoc />
    public IReadOnlyList<ProcessTableRow> ReadProcessTable() => throw new PlatformNotSupportedException(UnsupportedMessage);

    /// <inheritdoc />
    public ProcessDetails? ReadDetails(int pid, bool includeCommandLine) => throw new PlatformNotSupportedException(UnsupportedMessage);

    /// <inheritdoc />
    public IReadOnlyList<int> ReadContainmentMembers(int rootPid) => throw new PlatformNotSupportedException(UnsupportedMessage);

    /// <inheritdoc />
    public ProcessKillResult Kill(int pid) => throw new PlatformNotSupportedException(UnsupportedMessage);
}
