using System.Collections.Concurrent;

namespace HagiCode.Libs.Core.Discovery;

/// <summary>
/// Identifies one executable resolution. Every input that can change the answer is part of the key.
/// </summary>
/// <param name="Operation">The lookup: the path list, the provider lookup or the directory lookup.</param>
/// <param name="Subject">The provider name for a provider lookup; empty otherwise.</param>
/// <param name="Names">The ordered candidate names.</param>
/// <param name="Directories">The ordered directories of a directory lookup; empty otherwise.</param>
/// <param name="Path">The effective <c>PATH</c>.</param>
/// <param name="PathExt">The effective <c>PATHEXT</c>.</param>
/// <param name="AgentCliPath">The effective <c>HAGICODE_AGENT_CLI_PATH</c>.</param>
/// <param name="WorkingDirectory">The current directory, which is the first probe tier.</param>
/// <param name="ProcessDirectory">The directory of the running process, which is the first probe tier.</param>
/// <param name="IsWindows">The Windows flag. A fake-Windows resolver never reads a real-platform entry.</param>
internal readonly record struct CliResolutionKey(
    string Operation,
    string Subject,
    string Names,
    string Directories,
    string Path,
    string PathExt,
    string AgentCliPath,
    string WorkingDirectory,
    string ProcessDirectory,
    bool IsWindows);

/// <summary>
/// A found resolution: the paths to revalidate on every hit, the provider result when there is one, and when it stops being reused.
/// </summary>
internal sealed record CliResolutionEntry(IReadOnlyList<string> Paths, CliExecutableLookupResult? Lookup, DateTimeOffset ExpiresAt);

/// <summary>
/// Stores found executable resolutions for reuse. A not-found result is never stored, so an executable installed a moment
/// later is found by the very next lookup. Every hit is revalidated by the caller, and a found result expires after a bounded time.
/// </summary>
internal sealed class CliResolutionCache
{
    /// <summary>How long a found result is reused. It bounds how long a newer, higher-priority candidate can be shadowed.</summary>
    internal static readonly TimeSpan FoundResultLifetime = TimeSpan.FromSeconds(60);

    /// <summary>When the store holds more keys than this, expired entries are dropped.</summary>
    internal const int PruneThreshold = 512;

    private readonly ConcurrentDictionary<CliResolutionKey, CliResolutionEntry> _entries = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes an empty store that reads expiry from <paramref name="timeProvider" />.</summary>
    public CliResolutionCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Gets the process-wide store that every public <see cref="CliExecutableResolver" /> constructor shares.</summary>
    public static CliResolutionCache Shared { get; } = new(TimeProvider.System);

    /// <summary>Gets the number of keys currently held, including expired ones not yet dropped.</summary>
    public int Count => _entries.Count;

    /// <summary>Returns the entry for <paramref name="key"/> when it has not expired. An expired entry is removed.</summary>
    public bool TryGet(CliResolutionKey key, out CliResolutionEntry entry)
    {
        if (_entries.TryGetValue(key, out var found))
        {
            if (_timeProvider.GetUtcNow() < found.ExpiresAt)
            {
                entry = found;
                return true;
            }

            _entries.TryRemove(new KeyValuePair<CliResolutionKey, CliResolutionEntry>(key, found));
        }

        entry = null!;
        return false;
    }

    /// <summary>Removes the entry for <paramref name="key"/>, if any.</summary>
    public void Forget(CliResolutionKey key) => _entries.TryRemove(key, out _);

    /// <summary>Stores a found path list. An empty list is not found, so it is not stored.</summary>
    public void StorePaths(CliResolutionKey key, IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            Store(key, new CliResolutionEntry(paths, null, _timeProvider.GetUtcNow() + FoundResultLifetime));
        }
    }

    /// <summary>Stores a provider lookup that found a path. A lookup with no resolved path is not found, so it is not stored.</summary>
    public void StoreLookup(CliResolutionKey key, CliExecutableLookupResult lookup)
    {
        if (!lookup.IsFound)
        {
            return;
        }

        var paths = lookup.AcceptedPaths.Count > 0 ? lookup.AcceptedPaths : [lookup.ResolvedPath!];
        Store(key, new CliResolutionEntry(paths, lookup, _timeProvider.GetUtcNow() + FoundResultLifetime));
    }

    /// <summary>Removes every entry.</summary>
    public void Clear() => _entries.Clear();

    private void Store(CliResolutionKey key, CliResolutionEntry entry)
    {
        _entries[key] = entry;
        if (_entries.Count > PruneThreshold)
        {
            DropExpired();
        }
    }

    private void DropExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _entries)
        {
            if (now >= pair.Value.ExpiresAt)
            {
                _entries.TryRemove(pair);
            }
        }
    }
}
