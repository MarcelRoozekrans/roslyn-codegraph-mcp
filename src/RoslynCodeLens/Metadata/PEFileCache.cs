using System.Collections.Concurrent;
using ICSharpCode.Decompiler.Metadata;

namespace RoslynCodeLens.Metadata;

public sealed class PEFileCache : IDisposable
{
    private readonly ConcurrentDictionary<string, (DateTime Timestamp, PEFile File)> _cache
        = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public PEFile Get(string path)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PEFileCache));

        var stamp = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(path, out var existing) && existing.Timestamp == stamp)
            return existing.File;

        var pe = new PEFile(path);
        var newEntry = (stamp, pe);
        _cache.AddOrUpdate(path, newEntry, (_, old) =>
        {
            if (!ReferenceEquals(old.File, pe))
                old.File.Dispose();
            return newEntry;
        });
        return pe;
    }

    public void Invalidate(string path)
    {
        if (_cache.TryRemove(path, out var entry))
            entry.File.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _cache.Values)
            entry.File.Dispose();
        _cache.Clear();
    }
}
