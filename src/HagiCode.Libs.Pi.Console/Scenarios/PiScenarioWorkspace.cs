namespace HagiCode.Libs.Pi.Console.Scenarios;

internal sealed class PiScenarioWorkspace : IDisposable
{
    private readonly bool _ownsRootDirectory;
    private bool _disposed;

    private PiScenarioWorkspace(string rootDirectory, string workingDirectory, string sessionDirectory, bool ownsRootDirectory)
    {
        RootDirectory = rootDirectory;
        WorkingDirectory = workingDirectory;
        SessionDirectory = sessionDirectory;
        _ownsRootDirectory = ownsRootDirectory;
    }

    public string RootDirectory { get; }

    public string WorkingDirectory { get; }

    public string SessionDirectory { get; }

    public static PiScenarioWorkspace Create(string? workingDirectoryOverride, string? sessionDirectoryOverride)
    {
        var hasWorkingOverride = !string.IsNullOrWhiteSpace(workingDirectoryOverride);
        var rootDirectory = hasWorkingOverride
            ? Path.GetFullPath(workingDirectoryOverride!)
            : Path.Combine(Path.GetTempPath(), $"hagicode-libs-pi-console-{Guid.NewGuid():N}");
        var workingDirectory = hasWorkingOverride
            ? Path.GetFullPath(workingDirectoryOverride!)
            : Path.Combine(rootDirectory, "workspace");
        var sessionDirectory = !string.IsNullOrWhiteSpace(sessionDirectoryOverride)
            ? Path.GetFullPath(sessionDirectoryOverride!)
            : Path.Combine(rootDirectory, ".pi-console-sessions");

        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(sessionDirectory);

        var readmePath = Path.Combine(workingDirectory, "README.md");
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(readmePath, "# pi console workspace\n");
        }

        return new PiScenarioWorkspace(rootDirectory, workingDirectory, sessionDirectory, ownsRootDirectory: !hasWorkingOverride);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsRootDirectory && Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
    }
}
