using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SegmentStitch;

/// <summary>
/// Remembers keys until they expire: which items were fetched lately and which lookups a source
/// had nothing for. Persisted to a file; best-effort, so an I/O failure degrades to memory only.
/// Taken from TheIntroDB's Jellyfin plugin.
/// </summary>
public sealed class LookupCache
{
    private const int FlushThreshold = 500;

    private static readonly object FileLock = new();

    private readonly ConcurrentDictionary<string, DateTime> _entries = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private readonly ILogger _logger;
    private int _unsavedChanges;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookupCache"/> class.
    /// </summary>
    /// <param name="filePath">Path of the persistence file, or null for a memory-only cache.</param>
    /// <param name="logger">Logger used for persistence failures.</param>
    public LookupCache(string? filePath, ILogger? logger = null)
    {
        _filePath = filePath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Removes all entries, in memory and on disk.
    /// </summary>
    /// <returns>The number of entries removed.</returns>
    public int Clear()
    {
        lock (FileLock)
        {
            var cleared = _entries.Count;
            _entries.Clear();
            Interlocked.Exchange(ref _unsavedChanges, 0);

            if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
            {
                try
                {
                    File.Delete(_filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete lookup cache file {CachePath}", _filePath);
                }
            }

            return cleared;
        }
    }

    /// <summary>
    /// Returns true when the key holds an entry that has not expired.
    /// </summary>
    /// <param name="key">Lookup identity .</param>
    /// <returns>True when a not-expired entry exists.</returns>
    public bool TryGetHit(string key)
    {
        EnsureLoaded();
        if (!_entries.TryGetValue(key, out var expiryUtc))
        {
            return false;
        }

        if (DateTime.UtcNow < expiryUtc)
        {
            return true;
        }

        _entries.TryRemove(key, out _);
        MarkDirty();
        return false;
    }

    /// <summary>
    /// Records an entry for the given key that holds for <paramref name="ttl"/>.
    /// </summary>
    /// <param name="key">Entry key.</param>
    /// <param name="ttl">How long the entry holds.</param>
    public void Remember(string key, TimeSpan ttl)
    {
        EnsureLoaded();
        _entries[key] = DateTime.UtcNow.Add(ttl);
        MarkDirty();
    }

    /// <summary>
    /// Persists the cache to disk. A no-op when no file path was configured.
    /// </summary>
    public void Save()
    {
        if (_filePath is null)
        {
            return;
        }

        lock (FileLock)
        {
            try
            {
                var now = DateTime.UtcNow;
                var liveEntries = _entries
                    .Where(pair => pair.Value > now)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var tempPath = _filePath + ".tmp";
                var json = JsonSerializer.Serialize(liveEntries);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
                Interlocked.Exchange(ref _unsavedChanges, 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save lookup cache to {CachePath}", _filePath);
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (FileLock)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            if (_filePath is null || !File.Exists(_filePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var persisted = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                if (persisted is null)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                foreach (var pair in persisted)
                {
                    if (pair.Value > now)
                    {
                        _entries[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load lookup cache from {CachePath}", _filePath);
            }
        }
    }

    private void MarkDirty()
    {
        if (Interlocked.Increment(ref _unsavedChanges) >= FlushThreshold)
        {
            Save();
        }
    }
}
