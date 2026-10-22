using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.Core.Tests.Discovery;

/// <summary>
/// An in-memory file system for resolver tests. It counts every read, so a test can show what a lookup did or did not touch.
/// With <c>caseInsensitive</c> set, names match the way a Windows file system matches them.
/// </summary>
internal sealed class InMemoryCliFileSystem : ICliFileSystem
{
    private readonly StringComparer _comparer;
    private readonly StringComparison _comparison;
    private readonly HashSet<string> _files;
    private readonly HashSet<string> _directories;
    private readonly HashSet<string> _unreadable;
    private readonly Dictionary<string, int> _listingsByDirectory;

    /// <summary>Creates an empty file system. With <paramref name="caseInsensitive" />, names compare the way Windows compares them.</summary>
    public InMemoryCliFileSystem(bool caseInsensitive)
    {
        _comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _files = new HashSet<string>(_comparer);
        _directories = new HashSet<string>(_comparer);
        _unreadable = new HashSet<string>(_comparer);
        _listingsByDirectory = new Dictionary<string, int>(_comparer);
    }

    public int FileExistsCalls { get; private set; }

    public int DirectoryExistsCalls { get; private set; }

    public int ListFilesCalls { get; private set; }

    public void AddFile(string path)
    {
        _files.Add(path);
        AddDirectory(Path.GetDirectoryName(path)!);
    }

    public void RemoveFile(string path) => _files.Remove(path);

    public void AddDirectory(string path) => _directories.Add(path);

    public void MarkUnreadable(string directory) => _unreadable.Add(directory);

    public void ResetCounts()
    {
        FileExistsCalls = 0;
        DirectoryExistsCalls = 0;
        ListFilesCalls = 0;
        _listingsByDirectory.Clear();
    }

    /// <summary>Returns how many times a directory was listed since the last reset.</summary>
    public int ListingsOf(string directory) => _listingsByDirectory.GetValueOrDefault(directory);

    public bool FileExists(string path)
    {
        FileExistsCalls++;
        return _files.Contains(path);
    }

    public bool DirectoryExists(string path)
    {
        DirectoryExistsCalls++;
        return _directories.Contains(path);
    }

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern)
    {
        ListFilesCalls++;
        _listingsByDirectory[directory] = ListingsOf(directory) + 1;
        if (_unreadable.Contains(directory))
        {
            throw new UnauthorizedAccessException(directory);
        }

        if (!_directories.Contains(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        // The resolver only asks for "<name>.*", so the pattern is a prefix match.
        var prefix = searchPattern[..^1];
        return _files
            .Where(file => string.Equals(Path.GetDirectoryName(file), directory, _comparison)
                           && Path.GetFileName(file).StartsWith(prefix, _comparison))
            .ToArray();
    }
}

/// <summary>A clock that moves only when a test advances it.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
