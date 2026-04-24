using ICSharpCode.Decompiler.Metadata;
using RoslynCodeLens.Metadata;

namespace RoslynCodeLens.Tests.Metadata;

public class PEFileCacheTests
{
    [Fact]
    public void Get_SamePath_ReturnsSameInstance()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);
        var second = cache.Get(path);

        Assert.Same(first, second);
    }

    [Fact]
    public void Invalidate_DropsEntry()
    {
        var cache = new PEFileCache();
        var path = typeof(object).Assembly.Location;

        var first = cache.Get(path);
        cache.Invalidate(path);
        var second = cache.Get(path);

        Assert.NotSame(first, second);
    }
}
