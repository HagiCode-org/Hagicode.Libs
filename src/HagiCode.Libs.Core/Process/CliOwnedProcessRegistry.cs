using System.Text.Json;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Provides JSON persistence for managed CLI subprocess ownership records.
/// </summary>
public sealed class CliOwnedProcessRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Reads the persisted owned-process state file.
    /// </summary>
    public async Task<IReadOnlyList<CliOwnedProcessState>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds or updates a persisted owned-process record.
    /// </summary>
    public async Task AddOrUpdateAsync(string path, CliOwnedProcessState state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var states = (await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false)).ToList();
            states.RemoveAll(existing => existing.Pid == state.Pid);
            states.Add(state);
            await WriteUnsafeAsync(path, states, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a persisted owned-process record when present.
    /// </summary>
    public async Task<bool> RemoveAsync(string path, CliOwnedProcessState state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var states = (await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false)).ToList();
            var removed = states.RemoveAll(existing => MatchesIdentity(existing, state)) > 0;
            if (!removed)
            {
                return false;
            }

            await WriteUnsafeAsync(path, states, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes the persisted state file when it exists.
    /// </summary>
    public async Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool MatchesIdentity(CliOwnedProcessState left, CliOwnedProcessState right)
    {
        return left.Pid == right.Pid
               && left.StartedAtUtc == right.StartedAtUtc;
    }

    private static async Task<IReadOnlyList<CliOwnedProcessState>> ReadUnsafeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<CliOwnedProcessRegistryDocument>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return document?.Processes ?? [];
    }

    private static async Task WriteUnsafeAsync(
        string path,
        IReadOnlyCollection<CliOwnedProcessState> states,
        CancellationToken cancellationToken)
    {
        if (states.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            new CliOwnedProcessRegistryDocument(states.ToArray()),
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record CliOwnedProcessRegistryDocument(IReadOnlyList<CliOwnedProcessState> Processes);
}
