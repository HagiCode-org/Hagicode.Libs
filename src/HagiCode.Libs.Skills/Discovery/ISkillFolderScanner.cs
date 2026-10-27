namespace HagiCode.Libs.Skills.Discovery;

/// <summary>Finds skill folders one level below a folder. Scanning never changes the folder.</summary>
public interface ISkillFolderScanner
{
    /// <summary>
    /// Scans the folder synchronously. I/O problems are returned as a failed result, not thrown.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    SkillFolderScanResult Scan(string folderPath, CancellationToken cancellationToken = default);

    /// <summary>Scans the folder on a worker thread. Same contract as <see cref="Scan" />.</summary>
    Task<SkillFolderScanResult> ScanAsync(string folderPath, CancellationToken cancellationToken = default);
}
