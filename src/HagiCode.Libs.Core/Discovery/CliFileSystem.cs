namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// The file-system reads that executable resolution makes. The seam lets tests count the existence checks a lookup performs.
/// </summary>
internal interface ICliFileSystem
{
    /// <summary>Returns whether a file exists at <paramref name="path" />.</summary>
    bool FileExists(string path);

    /// <summary>Returns whether a directory exists at <paramref name="path" />.</summary>
    bool DirectoryExists(string path);

    /// <summary>Lists the files in a directory that match a search pattern, fully read.</summary>
    /// <exception cref="IOException">The directory is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory cannot be read.</exception>
    IReadOnlyList<string> ListFiles(string directory, string searchPattern);
}

/// <summary>
/// The <see cref="System.IO" /> implementation of <see cref="ICliFileSystem" />.
/// </summary>
internal sealed class SystemCliFileSystem : ICliFileSystem
{
    /// <summary>Gets the shared instance.</summary>
    public static SystemCliFileSystem Instance { get; } = new();

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) =>
        Directory.EnumerateFiles(directory, searchPattern).ToArray();
}
