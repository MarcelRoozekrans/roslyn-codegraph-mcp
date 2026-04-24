using System.Collections.Concurrent;
using ICSharpCode.Decompiler.Metadata;

namespace RoslynCodeLens.Metadata;

public sealed class PEFileCache : IDisposable
{
    private readonly ConcurrentDictionary<string, (DateTime Timestamp, PEFile File)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public PEFile Get(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(path, out var existing) && existing.Timestamp == stamp)
            return existing.File;

        var pe = new PEFile(path);
        _cache[path] = (stamp, pe);
        return pe;
    }

    public void Invalidate(string path)
    {
        if (_cache.TryRemove(path, out var entry))
            entry.File.Dispose();
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values)
            entry.File.Dispose();
        _cache.Clear();
    }
}
