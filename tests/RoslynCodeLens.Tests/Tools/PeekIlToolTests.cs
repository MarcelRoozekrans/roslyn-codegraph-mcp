using RoslynCodeLens;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class PeekIlToolTests : IDisposable
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;
    private readonly IlDisassemblerAdapter _adapter;
    private readonly PEFileCache _peCache;

    public PeekIlToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
        _peCache = new PEFileCache();
        _adapter = new IlDisassemblerAdapter(_peCache);
    }

    public void Dispose() { _peCache.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public void Peek_MetadataCtor_ReturnsIlText()
    {
        var result = PeekIlLogic.Execute(
            _loaded, _metadata, _adapter,
            "Microsoft.Extensions.DependencyInjection.ServiceDescriptor..ctor(System.Type, System.Type, Microsoft.Extensions.DependencyInjection.ServiceLifetime)");

        Assert.NotNull(result);
        Assert.Contains("IL_", result!.Il, StringComparison.Ordinal);
        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", result.AssemblyName);
    }

    [Fact]
    public void Peek_SourceSymbol_Throws()
    {
        Assert.Throws<ArgumentException>(() => PeekIlLogic.Execute(
            _loaded, _metadata, _adapter, "TestLib.Greeter.Greet"));
    }

    [Fact]
    public void Peek_AbstractMethod_Throws()
    {
        Assert.Throws<ArgumentException>(() => PeekIlLogic.Execute(
            _loaded, _metadata, _adapter,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection.Add"));
    }
}
